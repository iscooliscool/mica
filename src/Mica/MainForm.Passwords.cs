using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Mica
{
    // Password manager glue: messages from inject/mica-page.js, the save prompt, the fill suggestions
    // and the Passwords page. Rules:
    //  - The site's origin always comes from WebView2 (e.Source), never from what the page says.
    //  - Passwords only go back into a page after the person picks a login in Mica's own UI.
    //  - Showing, copying, editing or exporting a saved password needs Windows Hello / the Windows password.
    //  - Private windows never save.
    partial class MainForm
    {
        class PendingSave { public BrowserTab Tab; public string Origin, User, Pass; }
        readonly Dictionary<string, PendingSave> pendingSaves = new Dictionary<string, PendingSave>();
        DateTime authUntil = DateTime.MinValue;   // a successful check is good for a couple of minutes
        BrowserTab suggestTab;
        DateTime overlayClosedAt = DateTime.MinValue;

        void OnWebPageMessage(BrowserTab t, string source, Dictionary<string, object> m)
        {
            switch (m.Str("__mica"))
            {
                case "print":
                    if (t == active) OpenPrint();
                    break;

                case "pwSubmit":
                    {
                        if (Incognito || !store.Settings.Bool("savePasswords", true)) return;
                        var origin = Passwords.OriginOf(source);
                        var pass = m.Str("pass");
                        if (origin == null || string.IsNullOrEmpty(pass) || pass.Length > 512) return;
                        var user = (m.Str("user") ?? "").Trim();
                        if (user.Length > 256) user = user.Substring(0, 256);
                        if (Passwords.IsNever(origin)) return;
                        var state = Passwords.Check(origin, user, pass);
                        if (state == "same") return;
                        var token = Guid.NewGuid().ToString("N");
                        // keep at most a few prompts waiting
                        foreach (var k in pendingSaves.Where(p => p.Value.Tab == t).Select(p => p.Key).ToList()) pendingSaves.Remove(k);
                        pendingSaves[token] = new PendingSave { Tab = t, Origin = origin, User = user, Pass = pass };
                        if (t == active) ShowSavePrompt(token);
                        else t.PendingPwToken = token;
                        break;
                    }

                case "pwFocus":
                    {
                        if (t != active || t.View == null) return;
                        // a menu just closed and handed focus back to the page: not the person clicking a field
                        if ((DateTime.UtcNow - overlayClosedAt).TotalMilliseconds < 700) return;
                        var origin = Passwords.OriginOf(source);
                        var list = Passwords.For(origin);
                        if (list.Count == 0) return;
                        // field rect from the page (CSS px) → Mica window device px
                        double z = 1; try { z = t.View.ZoomFactor; } catch { }
                        double dpr = m.Num("dpr", 1);
                        var r = m.Sub("rect");
                        if (r == null) return;
                        int x = t.View.Left + (int)Math.Round(r.Num("x") * dpr * z);
                        int y = t.View.Top + (int)Math.Round(r.Num("y") * dpr * z);
                        int w = (int)Math.Round(r.Num("w") * dpr * z), h = (int)Math.Round(r.Num("h") * dpr * z);
                        suggestTab = t;
                        PostUi(new
                        {
                            type = "pwSuggest",
                            site = Passwords.HostOf(origin),
                            items = list.Take(8).Select(l => new { id = l.Id, user = string.IsNullOrEmpty(l.User) ? "(no username)" : l.User }),
                            rect = new { x, y, w, h },
                        });
                        break;
                    }

                case "pwInput":
                    if (t == active) PostUi(new { type = "pwSuggestClose" });
                    break;
            }
        }

        void ShowSavePrompt(string token)
        {
            if (!pendingSaves.TryGetValue(token, out var p)) return;
            var state = Passwords.Check(p.Origin, p.User, p.Pass);
            PostUi(new { type = "pwSave", token, site = Passwords.HostOf(p.Origin), user = p.User, update = state == "update" });
        }

        // the person picked a login in Mica's suggestion menu
        async Task FillLogin(string id)
        {
            var t = suggestTab;
            var l = Passwords.Get(id);
            if (t == null || t != active || l == null || t.View?.CoreWebView2 == null) return;
            // re-check the site at the moment of filling: it must still be the site the login belongs to
            if (Passwords.OriginOf(t.View.CoreWebView2.Source) != l.Origin) return;
            var js = "window.__micaFill&&window.__micaFill(" + J.Ser(l.User) + "," + J.Ser(l.Pass) + ")";
            try { await t.View.CoreWebView2.ExecuteScriptAsync(js); Passwords.Touch(l.Id); } catch { }
        }

        async Task<bool> Authenticate(string why)
        {
            if (DateTime.UtcNow < authUntil) return true;
            var ok = await WindowsAuth.Verify(Handle, why);
            if (ok) authUntil = DateTime.UtcNow.AddMinutes(2);
            return ok;
        }

        // ---- the Passwords page (mica://passwords) ----
        object PwListObject() => new
        {
            type = "pwList",
            list = Passwords.All().Select(l => new { id = l.Id, origin = l.Origin, site = Passwords.HostOf(l.Origin), user = l.User, used = l.Used }),
            never = Passwords.NeverList(),
            offer = store.Settings.Bool("savePasswords", true),
        };

        bool HandlePasswordPageCommand(BrowserTab t, string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "pwList": Post(t, PwListObject()); return true;
                case "pwReveal":
                    _ = Run(async () =>
                    {
                        if (!await Authenticate("Mica wants to show a saved password.")) { Post(t, new { type = "pwDenied" }); return; }
                        var l = Passwords.Get(m.Str("id"));
                        if (l != null) Post(t, new { type = "pwRevealed", id = l.Id, pass = l.Pass });
                    });
                    return true;
                case "pwCopy":
                    _ = Run(async () =>
                    {
                        if (!await Authenticate("Mica wants to copy a saved password.")) { Post(t, new { type = "pwDenied" }); return; }
                        var l = Passwords.Get(m.Str("id"));
                        if (l == null) return;
                        try { Clipboard.SetText(l.Pass); } catch { }
                        Post(t, new { type = "pwCopied" });
                        // clear it from the clipboard after a while, if it's still there
                        var copied = l.Pass;
                        await Task.Delay(45000);
                        try { if (Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); } catch { }
                    });
                    return true;
                case "pwSave":   // add or edit from the page
                    _ = Run(async () =>
                    {
                        var id = m.Str("id");
                        if (!string.IsNullOrEmpty(id))
                        {
                            if (!await Authenticate("Mica wants to change a saved password.")) { Post(t, new { type = "pwDenied" }); return; }
                            Passwords.Edit(id, m.Str("user"), m.Str("pass"));
                        }
                        else
                        {
                            var site = (m.Str("site") ?? "").Trim();
                            if (!site.Contains("://")) site = "https://" + site;
                            var origin = Passwords.OriginOf(site);
                            if (origin == null || string.IsNullOrEmpty(m.Str("pass"))) { Post(t, new { type = "pwError", text = "Enter a website and a password." }); return; }
                            Passwords.Upsert(origin, m.Str("user") ?? "", m.Str("pass"));
                        }
                        Post(t, PwListObject());
                    });
                    return true;
                case "pwDelete":
                    Passwords.Remove(m.Str("id"));
                    Post(t, PwListObject());
                    return true;
                case "pwNeverRemove":
                    Passwords.RemoveNever(m.Str("origin"));
                    Post(t, PwListObject());
                    return true;
                case "pwOffer":
                    SetSetting("savePasswords", m.Bool("on"), true);
                    Post(t, PwListObject());
                    return true;
                case "pwImport":
                    {
                        using (var dlg = new OpenFileDialog { Filter = "Passwords CSV|*.csv|All files|*.*", Title = "Import passwords" })
                        {
                            if (dlg.ShowDialog(this) != DialogResult.OK) return true;
                            try
                            {
                                var n = Passwords.ImportCsv(dlg.FileName);
                                Post(t, PwListObject());
                                Post(t, new { type = "pwImported", count = n });
                            }
                            catch (Exception ex) { Post(t, new { type = "pwError", text = ex.Message }); }
                        }
                        return true;
                    }
                case "pwExport":
                    _ = Run(async () =>
                    {
                        if (!await Authenticate("Mica wants to export your saved passwords.")) { Post(t, new { type = "pwDenied" }); return; }
                        using (var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "Mica Passwords.csv", Title = "Export passwords" })
                        {
                            if (dlg.ShowDialog(this) != DialogResult.OK) return;
                            try { Passwords.ExportCsv(dlg.FileName); Post(t, new { type = "pwExported" }); }
                            catch (Exception ex) { Post(t, new { type = "pwError", text = ex.Message }); }
                        }
                    });
                    return true;
            }
            return false;
        }

        // ---- answers from Mica's UI ----
        void HandlePasswordUiCommand(string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "pwFill": _ = FillLogin(m.Str("id")); break;
                case "pwSaveAnswer":
                    {
                        var token = m.Str("token");
                        if (!pendingSaves.TryGetValue(token, out var p)) break;
                        pendingSaves.Remove(token);
                        var a = m.Str("answer");
                        try
                        {
                            if (a == "save") { Passwords.Upsert(p.Origin, m.Str("user") ?? p.User, p.Pass); PostUi(new { type = "toast", text = "Password saved", icon = "key" }); }
                            else if (a == "never") Passwords.AddNever(p.Origin);
                        }
                        catch (Exception ex) { PostUi(new { type = "toast", text = "Couldn't save the password: " + ex.Message, icon = "info" }); }
                        break;
                    }
            }
        }
    }
}
