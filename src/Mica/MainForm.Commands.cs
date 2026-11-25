using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Mica
{
    public partial class MainForm
    {
        // =====================================================================
        //  Messages from the browser UI (tabs, toolbar, menus, tour)
        // =====================================================================
        void OnUiMessage(Dictionary<string, object> m)
        {
            var cmd = m.Str("cmd", "");
            var cw = active?.View?.CoreWebView2;
            switch (cmd)
            {
                case "ready":
                    uiReady = true;
                    overlay = false;
                    ApplyLayout();
                    PostUi(new { type = "sys", sys = SysObject() });
                    SendSettings();
                    SendBookmarks();
                    SendState();
                    if (store.B("showMemory")) SendMemory();
                    if (Incognito) break;
                    if (!store.B("firstRunDone")) PostUi(new { type = "startTour", first = true });
                    else _ = AutoCheckUpdate();
                    break;
                case "layout":
                    rectA = Rect(m.Sub("a")); rectB = Rect(m.Sub("b")); rectAi = Rect(m.Sub("ai"));
                    contentRadius = m.Int("radius");
                    ApplyLayout();
                    break;
                case "themeColors":
                    try { pageBg = ColorTranslator.FromHtml(m.Str("bg", "#ffffff")); BackColor = pageBg; } catch { }
                    break;

                // window
                case "min": WindowState = FormWindowState.Minimized; break;
                case "max": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case "close": Close(); break;
                case "drag": StartDrag(Native.HTCAPTION); break;
                case "resize":
                    var edge = m.Str("edge", "top");
                    StartDrag(edge == "topleft" ? Native.HTTOPLEFT : edge == "topright" ? Native.HTTOPRIGHT : Native.HTTOP);
                    break;
                case "overlay": SetOverlay(m.Bool("on")); break;
                case "raise": if (overlay) ui.BringToFront(); break;
                case "focusPage": if (!overlay) active?.View?.Focus(); break;

                // tabs
                case "newTab": _ = NewTab(Resolve(m.Str("url")) ?? "mica://newtab"); break;
                case "closeTab": CloseTab(Find(m.Int("id"))); break;
                case "activate": Activate(Find(m.Int("id"))); break;
                case "moveTab":
                    {
                        var t = Find(m.Int("id"));
                        if (t == null) break;
                        tabs.Remove(t);
                        tabs.Insert(Math.Max(0, Math.Min(m.Int("index"), tabs.Count)), t);
                        SendState();
                        break;
                    }
                case "duplicate": { var t = Find(m.Int("id")); if (t != null) _ = NewTab(t.Url, true, t); break; }
                case "closeOthers":
                    foreach (var t in tabs.Where(x => x.Id != m.Int("id")).ToList()) CloseTab(t);
                    break;
                case "closeRight":
                    {
                        var t = Find(m.Int("id"));
                        if (t == null) break;
                        foreach (var x in tabs.Skip(tabs.IndexOf(t) + 1).ToList()) CloseTab(x);
                        break;
                    }
                case "sleepTab": { var t = Find(m.Int("id")); if (t != null) Sleep(t); break; }
                case "sleepAll": foreach (var t in tabs.ToList()) Sleep(t); SendMemory(); break;
                case "mute":
                    {
                        var t = Find(m.Int("id"));
                        if (t?.View?.CoreWebView2 != null) t.View.CoreWebView2.IsMuted = !t.View.CoreWebView2.IsMuted;
                        break;
                    }
                case "reopen": ReopenClosed(); break;
                case "incognito": Program.OpenWindow(new string[0], true); break;

                // navigation
                case "navigate":
                    {
                        var url = Resolve(m.Str("text"));
                        if (url == null || active == null) break;
                        if (active.View == null) { active.Url = url; _ = Wake(active); }
                        else active.View.CoreWebView2.Navigate(ToNavigable(url));
                        active.View?.Focus();
                        break;
                    }
                case "back": if (cw != null && cw.CanGoBack) cw.GoBack(); break;
                case "forward": if (cw != null && cw.CanGoForward) cw.GoForward(); break;
                case "reload": cw?.Reload(); break;
                case "stop": cw?.Stop(); break;
                case "home": cw?.Navigate(ToNavigable("mica://newtab")); break;
                case "open": OpenInternal(m.Str("page", "settings")); break;
                case "suggest": PostUi(new { type = "suggest", seq = m.Int("seq"), items = Suggest(m.Str("q", "")) }); break;

                // tools
                case "bookmark": ToggleBookmarkActive(); break;
                case "devtools": cw?.OpenDevToolsWindow(); break;
                case "downloads": cw?.OpenDefaultDownloadDialog(); break;
                case "print": cw?.ShowPrintUI(); break;
                case "zoom":
                    if (active?.View != null)
                    {
                        var z = m.Str("dir");
                        double f = active.View.ZoomFactor;
                        f = z == "in" ? Math.Min(5, f + 0.1) : z == "out" ? Math.Max(0.25, f - 0.1) : 1.0;
                        active.View.ZoomFactor = f;
                        PostUi(new { type = "zoom", value = (int)Math.Round(f * 100) });
                    }
                    break;
                case "screenshot": _ = Screenshot(m.Str("mode", "visible")); break;
                case "pip": _ = PopOutVideo(); break;
                case "split": StartSplit(m.Int("id")); break;
                case "unsplit": splitA = splitB = null; ApplyLayout(); SendState(); break;
                case "swapSplit": if (splitA != null) { var x = splitA; splitA = splitB; splitB = x; ApplyLayout(); SendState(); } break;
                case "ai": ToggleAi(m.ContainsKey("open") ? m.Bool("open") : !aiOpen); break;
                case "aiProvider": SetSetting("aiProvider", m.Str("provider", "duck")); if (ai != null) ai.CoreWebView2?.Navigate(AiUrl()); break;
                case "summarize": _ = AskAi(null); break;
                case "tourDone": store.Set("firstRunDone", true); store.Flush(); break;
                case "update": _ = RunUpdate(); break;
                case "skipUpdate":
                    if (pendingUpdate != null) store.Set("skipVersion", pendingUpdate.Version);
                    pendingUpdate = null; SendState();
                    break;
                case "later": pendingUpdate = null; SendState(); break;
                default: HandleShared(cmd, m, msg => PostUi(msg)); break;
            }
        }

        // =====================================================================
        //  Messages from Mica's own pages (new tab, settings, history)
        // =====================================================================
        void OnPageMessage(BrowserTab t, Dictionary<string, object> m)
        {
            var cmd = m.Str("cmd", "");
            switch (cmd)
            {
                case "hello":
                    Post(t, new { type = "init", settings = store.Settings, sys = SysObject(), bookmarks = store.Bookmarks, incognito = Incognito });
                    break;
                case "navigate":
                    {
                        var url = Resolve(m.Str("text"));
                        if (url != null) t.View?.CoreWebView2.Navigate(ToNavigable(url));
                        break;
                    }
                case "history":
                    {
                        var q = m.Str("q", "").ToLowerInvariant();
                        var list = store.History.Where(h => q.Length == 0 || h.Str("url", "").ToLowerInvariant().Contains(q) || h.Str("title", "").ToLowerInvariant().Contains(q)).Take(m.Int("limit", 300)).ToList();
                        Post(t, new { type = "history", list });
                        break;
                    }
                case "deleteHistory":
                    {
                        var u = m.Str("url");
                        if (u == null) store.History.Clear(); else store.History.RemoveAll(h => h.Str("url") == u);
                        store.MarkDirty("history");
                        Post(t, new { type = "historyChanged" });
                        break;
                    }
                case "startTour":
                    SetOverlay(true);
                    PostUi(new { type = "startTour", first = false });
                    break;
                case "memory": SendMemory(); break;
                default: HandleShared(cmd, m, msg => Post(t, msg)); break;
            }
        }

        /// <summary>Commands both the UI and the internal pages can send.</summary>
        void HandleShared(string cmd, Dictionary<string, object> m, Action<object> reply)
        {
            switch (cmd)
            {
                case "setSetting":
                    SetSetting(m.Str("key"), m.TryGetValue("value", out var v) ? v : null);
                    break;
                case "setSettings":
                    var vals = m.Sub("values");
                    if (vals != null) foreach (var kv in vals) SetSetting(kv.Key, kv.Value, false);
                    SendSettings();
                    break;
                case "openUrl":
                    {
                        var url = Resolve(m.Str("url"));
                        if (url != null) _ = NewTab(url, !m.Bool("background"));
                        break;
                    }
                case "addBookmark":
                    if (!store.IsBookmarked(m.Str("url"))) store.ToggleBookmark(m.Str("url"), m.Str("title"));
                    SendBookmarks(); SendState();
                    break;
                case "removeBookmark":
                    store.Bookmarks.RemoveAll(b => b.Str("url") == m.Str("url"));
                    store.MarkDirty("bookmarks");
                    SendBookmarks(); SendState();
                    break;
                case "import":
                    {
                        int n;
                        try { n = Importer.Import(store, m.Str("from", "chrome")); } catch { n = -2; }
                        if (n > 0 && !store.B("bookmarksBar")) SetSetting("bookmarksBar", true);
                        SendBookmarks();
                        reply(new { type = "imported", from = m.Str("from"), count = n });
                        break;
                    }
                case "pickWallpaper": PickWallpaper(reply); break;
                case "clearData": _ = ClearData(m.Str("what", "cache"), reply); break;
                case "checkUpdate": _ = ManualCheckUpdate(reply); break;
                case "restart": Restart(); break;
                case "openFolder":
                    try { Process.Start("explorer.exe", m.Str("which") == "data" ? Store.Dir : ScreenshotDir()); } catch { }
                    break;
                case "setDefault":
                    try { Process.Start("ms-settings:defaultapps?registeredAppUser=Mica"); }
                    catch { try { Process.Start("ms-settings:defaultapps"); } catch { } }
                    break;
                case "startTour":
                    SetOverlay(true);
                    PostUi(new { type = "startTour", first = false });
                    break;
            }
        }

        void SetSetting(string key, object value, bool broadcast = true)
        {
            if (string.IsNullOrEmpty(key) || key == "window") return;
            store.Set(key, value);
            switch (key)
            {
                case "tracking": ApplyTracking(); break;
                case "showMemory": if (store.B("showMemory")) { memTimer.Start(); SendMemory(); } else memTimer.Stop(); break;
                case "aiEnabled": if (!store.B("aiEnabled")) ToggleAi(false); break;
                case "pipButton": _ = UpdatePipScripts(); break;
            }
            if (broadcast) SendSettings();
        }

        async Task UpdatePipScripts()
        {
            bool on = store.B("pipButton");
            foreach (var t in tabs.Where(x => x.View?.CoreWebView2 != null))
            {
                var c = t.View.CoreWebView2;
                if (on && t.PipScriptId == null && pipScript.Length > 0) t.PipScriptId = await c.AddScriptToExecuteOnDocumentCreatedAsync(pipScript);
                else if (!on && t.PipScriptId != null) { c.RemoveScriptToExecuteOnDocumentCreated(t.PipScriptId); t.PipScriptId = null; }
            }
        }

        void OpenInternal(string page)
        {
            var url = "mica://" + page;
            var existing = tabs.FirstOrDefault(t => t.Url == url || (page == "settings" && t.Url.StartsWith("mica://settings")));
            if (existing != null) { Activate(existing); return; }
            if (active != null && active.Url == "mica://newtab" && active.View != null) active.View.CoreWebView2.Navigate(ToNavigable(url));
            else _ = NewTab(url);
        }

        List<object> Suggest(string q)
        {
            q = q.Trim().ToLowerInvariant();
            var outList = new List<object>();
            if (q.Length == 0) return outList;
            var seen = new HashSet<string>();
            IEnumerable<Dictionary<string, object>> Match(IEnumerable<Dictionary<string, object>> src) =>
                src.Where(x => x.Str("url", "").ToLowerInvariant().Contains(q) || x.Str("title", "").ToLowerInvariant().Contains(q));
            foreach (var b in Match(store.Bookmarks).Take(3))
                if (seen.Add(b.Str("url"))) outList.Add(new { kind = "bookmark", url = b.Str("url"), title = b.Str("title") });
            foreach (var h in Match(store.History).OrderByDescending(x => x.Int("visits")).Take(8))
            {
                if (outList.Count >= 6) break;
                if (seen.Add(h.Str("url"))) outList.Add(new { kind = "history", url = h.Str("url"), title = h.Str("title") });
            }
            return outList;
        }

        void ToggleBookmarkActive()
        {
            var c = active?.View?.CoreWebView2;
            if (c == null || active.Url.StartsWith("mica://")) return;
            bool added = store.ToggleBookmark(c.Source, c.DocumentTitle);
            PostUi(new { type = "toast", text = added ? "Bookmarked" : "Bookmark removed", icon = "star" });
            SendBookmarks();
            SendState();
        }

        // =====================================================================
        //  Overlay: menus and the tour need to draw over the page, so we briefly
        //  bring the UI to the front with a snapshot of the page behind it.
        // =====================================================================
        async void SetOverlay(bool on)
        {
            if (on == overlay) { if (on) ui.BringToFront(); return; }
            overlay = on;
            if (!on) { ui.SendToBack(); if (active?.View != null && !fullscreen) active.View.Focus(); return; }

            var shots = new List<object>();
            if (!store.B("potato"))
            {
                async Task Snap(string key, WebView2 v)
                {
                    if (v?.CoreWebView2 == null || !v.Visible) return;
                    try
                    {
                        using (var ms = new MemoryStream())
                        {
                            await v.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, ms);
                            shots.Add(new { key, src = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()) });
                        }
                    }
                    catch { }
                }
                if (splitA != null) { await Snap("a", splitA.View); await Snap("b", splitB.View); }
                else await Snap("a", active?.View);
                if (aiOpen) await Snap("ai", ai);
            }
            if (!overlay) return; // closed while we were capturing
            PostUi(new { type = "backdrop", shots });
        }

        // =====================================================================
        //  Keyboard shortcuts (work no matter what has focus)
        // =====================================================================
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            => HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

        // The WinForms WebView2 control doesn't pass shortcut keys up to the form, so we listen
        // to its controller's AcceleratorKeyPressed event directly.
        static readonly System.Reflection.FieldInfo controllerField =
            typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        void HookKeys(WebView2 v)
        {
            if (!(controllerField?.GetValue(v) is CoreWebView2Controller ctl)) return;
            ctl.AcceleratorKeyPressed += (s, e) =>
            {
                if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown) return;
                if (e.PhysicalKeyStatus.WasKeyDown != 0 && (e.VirtualKey == (uint)Keys.T || e.VirtualKey == (uint)Keys.W)) { e.Handled = true; return; } // no auto-repeat floods
                var k = (Keys)e.VirtualKey | ModifierKeys;
                if (HandleKey(k)) e.Handled = true;
            };
        }

        bool HandleKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.T:
                case Keys.Control | Keys.N: _ = NewTab("mica://newtab"); return true;
                case Keys.Control | Keys.W:
                case Keys.Control | Keys.F4: CloseTab(active); return true;
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown: Cycle(1); return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp: Cycle(-1); return true;
                case Keys.Control | Keys.L:
                case Keys.Alt | Keys.D:
                case Keys.F6: PostUi(new { type = "focusAddress" }); ui.Focus(); return true;
                case Keys.Control | Keys.Shift | Keys.T: ReopenClosed(); return true;
                case Keys.Control | Keys.Shift | Keys.N: Program.OpenWindow(new string[0], true); return true;
                case Keys.Control | Keys.D: ToggleBookmarkActive(); return true;
                case Keys.Control | Keys.H: OpenInternal("history"); return true;
                case Keys.Control | Keys.Shift | Keys.O: OpenInternal("bookmarks"); return true;
                case Keys.Control | Keys.Oemcomma: OpenInternal("settings"); return true;
                case Keys.Control | Keys.J: active?.View?.CoreWebView2?.OpenDefaultDownloadDialog(); return true;
                case Keys.Control | Keys.Shift | Keys.A: if (store.B("aiEnabled")) ToggleAi(!aiOpen); else OpenInternal("settings#ai"); return true;
                case Keys.Control | Keys.Shift | Keys.S: _ = Screenshot("visible"); return true;
                case Keys.Control | Keys.Shift | Keys.I:
                case Keys.F12: active?.View?.CoreWebView2?.OpenDevToolsWindow(); return true;
                case Keys.F11:
                    if (fullscreen) { if (fsTab?.View?.CoreWebView2?.ContainsFullScreenElement == true) return false; SetFullscreen(fsTab, false); }
                    else SetFullscreen(active, true);
                    return true;
                case Keys.Escape:
                    if (fullscreen && fsTab?.View?.CoreWebView2?.ContainsFullScreenElement != true) { SetFullscreen(fsTab, false); return true; }
                    break;
                case Keys.F5:
                case Keys.Control | Keys.R: active?.View?.CoreWebView2?.Reload(); return true;
                case Keys.Alt | Keys.Left: { var c = active?.View?.CoreWebView2; if (c != null && c.CanGoBack) c.GoBack(); return true; }
                case Keys.Alt | Keys.Right: { var c = active?.View?.CoreWebView2; if (c != null && c.CanGoForward) c.GoForward(); return true; }
            }
            if ((keyData & Keys.Control) == Keys.Control && (keyData & ~Keys.Control) >= Keys.D1 && (keyData & ~Keys.Control) <= Keys.D9)
            {
                int n = (int)(keyData & ~Keys.Control) - (int)Keys.D1;
                if (tabs.Count > 0) Activate(n == 8 ? tabs.Last() : tabs[Math.Min(n, tabs.Count - 1)]);
                return true;
            }
            return false;
        }

        // =====================================================================
        //  Right-click menu on web pages
        // =====================================================================
        void PageContextMenu(BrowserTab t, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var target = e.ContextMenuTarget;
            var items = new List<CoreWebView2ContextMenuItem>();
            if (store.B("aiEnabled"))
            {
                if (target.HasSelection && !string.IsNullOrWhiteSpace(target.SelectionText))
                {
                    var sel = target.SelectionText.Trim();
                    var label = sel.Length > 28 ? sel.Substring(0, 28) + "…" : sel;
                    var it = env.CreateContextMenuItem("Ask Mica AI about “" + label.Replace("&", "&&") + "”", null, CoreWebView2ContextMenuItemKind.Command);
                    it.CustomItemSelected += (s, a) => BeginInvoke(new Action(() => _ = AskAi("Explain this:\n\n" + sel)));
                    items.Add(it);
                }
                else if (!target.IsEditable)
                {
                    var it = env.CreateContextMenuItem("Summarize this page with AI", null, CoreWebView2ContextMenuItemKind.Command);
                    it.CustomItemSelected += (s, a) => BeginInvoke(new Action(() => { Activate(t); _ = AskAi(null); }));
                    items.Add(it);
                }
            }
            if (target.HasLinkUri)
            {
                var link = target.LinkUri;
                var it = env.CreateContextMenuItem("Open link in split screen", null, CoreWebView2ContextMenuItemKind.Command);
                it.CustomItemSelected += (s, a) => BeginInvoke(new Action(() => _ = SplitWithUrl(t, link)));
                items.Add(it);
            }
            if (items.Count == 0) return;
            items.Add(env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            for (int i = items.Count - 1; i >= 0; i--) e.MenuItems.Insert(0, items[i]);
        }

        // =====================================================================
        //  Split screen
        // =====================================================================
        void StartSplit(int otherId)
        {
            if (active == null) return;
            var other = Find(otherId);
            if (other == null || other == active)
            {
                var nt = new BrowserTab { Url = "mica://newtab" };
                tabs.Insert(tabs.IndexOf(active) + 1, nt);
                other = nt;
            }
            splitA = active;
            splitB = other;
            if (other.View == null) _ = Wake(other);
            other.LastActive = DateTime.Now;
            ApplyLayout();
            SendState();
        }

        async Task SplitWithUrl(BrowserTab left, string url)
        {
            var nt = new BrowserTab { Url = url, Title = "Loading…" };
            tabs.Insert(tabs.IndexOf(left) + 1, nt);
            active = left;
            splitA = left;
            splitB = nt;
            await Wake(nt);
            ApplyLayout();
            SendState();
        }

        // =====================================================================
        //  AI side panel (free, no login: duck.ai or chatgpt.com, loaded as normal web pages)
        // =====================================================================
        string AiUrl() => store.S("aiProvider") == "chatgpt" ? "https://chatgpt.com/" : "https://duck.ai/";

        void ToggleAi(bool open)
        {
            if (open && !store.B("aiEnabled")) { OpenInternal("settings#ai"); return; }
            aiOpen = open;
            if (open && ai == null) _ = CreateAi();
            if (!open && ai != null)
            {
                // Closing the panel frees its memory completely.
                Controls.Remove(ai);
                ai.Dispose();
                ai = null;
                rectAi = Rectangle.Empty;
            }
            ApplyLayout();
            SendState();
        }

        TaskCompletionSource<bool> aiLoaded;

        async Task CreateAi()
        {
            var v = new WebView2 { Visible = false, DefaultBackgroundColor = Color.White };
            ai = v;
            aiLoaded = new TaskCompletionSource<bool>();
            Controls.Add(v);
            _ = v.Handle;
            await v.EnsureCoreWebView2Async(env, ControllerOptions());
            if (ai != v) return;
            HookKeys(v);
            var c = v.CoreWebView2;
            c.Settings.IsStatusBarEnabled = false;
            c.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTab(e.Uri); };
            c.NavigationCompleted += (s, e) => aiLoaded?.TrySetResult(true);
            c.Navigate(AiUrl());
            ApplyLayout();
        }

        /// <summary>Opens the AI panel and types a question into it. With no question, summarizes the current page.</summary>
        async Task AskAi(string prompt)
        {
            if (!store.B("aiEnabled")) { OpenInternal("settings#ai"); return; }
            if (prompt == null)
            {
                var c = active?.View?.CoreWebView2;
                if (c == null || active.Url.StartsWith("mica://")) { PostUi(new { type = "toast", text = "Open a web page first", icon = "ai" }); return; }
                string text = "";
                try
                {
                    var json = await c.ExecuteScriptAsync("(document.body ? document.body.innerText : '').replace(/\\n{3,}/g, '\\n\\n').slice(0, 9000)");
                    text = J.S.Deserialize<string>(json) ?? "";
                }
                catch { }
                prompt = "Summarize this web page in a few short bullet points.\n\nTitle: " + c.DocumentTitle + "\nURL: " + c.Source + "\n\n" + text;
            }
            if (!aiOpen) ToggleAi(true);
            var deadline = DateTime.Now.AddSeconds(15);
            while ((ai?.CoreWebView2 == null || aiLoaded == null || !aiLoaded.Task.IsCompleted) && DateTime.Now < deadline) await Task.Delay(200);

            string script = "(" + InsertPromptJs + ")(" + J.Ser(prompt) + ")";
            string result = "none";
            for (int i = 0; i < 12 && ai?.CoreWebView2 != null; i++)
            {
                try { result = J.S.Deserialize<string>(await ai.CoreWebView2.ExecuteScriptAsync(script)) ?? "none"; } catch { }
                if (result != "none") break;
                await Task.Delay(500);
            }
            if (result == "none")
            {
                try { Clipboard.SetText(prompt); } catch { }
                PostUi(new { type = "toast", text = "Copied to clipboard. Paste it into the AI chat.", icon = "ai" });
            }
            ai?.Focus();
        }

        const string InsertPromptJs = @"function(t){
  const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const el = document.querySelector('#prompt-textarea') || document.querySelector('textarea[name=""user-prompt""]')
    || [...document.querySelectorAll('textarea')].filter(vis)[0] || [...document.querySelectorAll('[contenteditable=""true""]')].filter(vis)[0];
  if (!el) return 'none';
  el.focus();
  if (el.tagName === 'TEXTAREA') {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, t);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  } else {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, t);
  }
  setTimeout(() => {
    const b = document.querySelector('[data-testid=""send-button""]') || document.querySelector('button[type=""submit""]:not([disabled])')
      || [...document.querySelectorAll('button[aria-label]')].find(x => /send/i.test(x.getAttribute('aria-label')) && !x.disabled);
    if (b) b.click();
  }, 450);
  return 'ok';
}";

        // =====================================================================
        //  Screenshots + picture-in-picture
        // =====================================================================
        static string ScreenshotDir()
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Mica Screenshots");
            Directory.CreateDirectory(d);
            return d;
        }

        async Task Screenshot(string mode)
        {
            var c = active?.View?.CoreWebView2;
            if (c == null) return;
            try
            {
                byte[] png;
                if (mode == "full")
                {
                    var metrics = J.Obj(await c.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
                    var size = metrics.Sub("cssContentSize") ?? metrics.Sub("contentSize");
                    double w = size.Num("width", 1280), h = Math.Min(size.Num("height", 800), 16000);
                    var args = J.Ser(new { format = "png", captureBeyondViewport = true, clip = new { x = 0, y = 0, width = w, height = h, scale = 1 } });
                    var res = J.Obj(await c.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args));
                    png = Convert.FromBase64String(res.Str("data", ""));
                }
                else
                {
                    using (var ms = new MemoryStream())
                    {
                        await c.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                        png = ms.ToArray();
                    }
                }
                var file = Path.Combine(ScreenshotDir(), "Mica " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png");
                File.WriteAllBytes(file, png);
                try { using (var ms = new MemoryStream(png)) using (var img = Image.FromStream(ms)) Clipboard.SetImage(img); } catch { }
                PostUi(new { type = "toast", text = "Screenshot saved and copied", icon = "camera", action = "openShots" });
            }
            catch (Exception ex)
            {
                PostUi(new { type = "toast", text = "Couldn't take screenshot: " + ex.Message, icon = "camera" });
            }
        }

        async Task PopOutVideo()
        {
            var c = active?.View?.CoreWebView2;
            if (c == null) return;
            const string js = @"(async () => {
  const vids = [...document.querySelectorAll('video')].filter(v => v.readyState > 0);
  if (!vids.length) return 'none';
  if (document.pictureInPictureElement) { await document.exitPictureInPicture(); return 'closed'; }
  vids.sort((a, b) => (b.paused ? 0 : 1) - (a.paused ? 0 : 1) || b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight);
  vids[0].disablePictureInPicture = false;
  await vids[0].requestPictureInPicture();
  return 'ok';
})()";
            try
            {
                var res = J.Obj(await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", J.Ser(new { expression = js, userGesture = true, awaitPromise = true, returnByValue = true })));
                var val = res.Sub("result")?.Str("value");
                if (val == "none") PostUi(new { type = "toast", text = "No video on this page", icon = "pip" });
            }
            catch { }
        }

        // =====================================================================
        //  Misc
        // =====================================================================
        void PickWallpaper(Action<object> reply)
        {
            using (var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp", Title = "Choose a wallpaper" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    foreach (var old in Directory.GetFiles(Store.WallDir)) try { File.Delete(old); } catch { }
                    var name = "wall-" + J.Now() + Path.GetExtension(dlg.FileName).ToLowerInvariant();
                    File.Copy(dlg.FileName, Path.Combine(Store.WallDir, name), true);
                    SetSetting("wallpaper", name);
                }
                catch (Exception ex) { reply(new { type = "toast", text = "Couldn't use that image: " + ex.Message }); }
            }
        }

        async Task ClearData(string what, Action<object> reply)
        {
            try
            {
                var profile = ui.CoreWebView2.Profile;
                if (what == "history") { store.History.Clear(); store.MarkDirty("history"); }
                else if (what == "cookies") await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.AllDomStorage);
                else if (what == "cache") await profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
                else if (what == "all")
                {
                    store.History.Clear(); store.MarkDirty("history");
                    await profile.ClearBrowsingDataAsync();
                }
                reply(new { type = "cleared", what });
            }
            catch (Exception ex) { reply(new { type = "toast", text = "Couldn't clear: " + ex.Message }); }
        }

        void Restart()
        {
            SaveEverything();
            try { Process.Start(Application.ExecutablePath, "--restarted"); } catch { }
            Environment.Exit(0);
        }

        async Task AutoCheckUpdate()
        {
            if (!store.B("updateCheck")) return;
            double last = store.N("lastUpdateCheck");
            if (J.Now() - last < 20 * 3600 * 1000) return;
            store.Set("lastUpdateCheck", (double)J.Now());
            try
            {
                var info = await Updater.Check();
                if (info != null && info.Version != store.S("skipVersion")) { pendingUpdate = info; SendState(); }
            }
            catch { }
        }

        async Task ManualCheckUpdate(Action<object> reply)
        {
            try
            {
                var info = await Updater.Check();
                if (info != null) { pendingUpdate = info; SendState(); }
                reply(new { type = "updateResult", version = info?.Version, current = Updater.CurrentText });
            }
            catch (Exception ex) { reply(new { type = "updateResult", error = ex.Message, current = Updater.CurrentText }); }
        }

        async Task RunUpdate()
        {
            var info = pendingUpdate;
            if (info == null) return;
            try
            {
                int lastPct = -1;
                var path = await Updater.Download(info, pct =>
                {
                    if (pct == lastPct) return;
                    lastPct = pct;
                    BeginInvoke(new Action(() => PostUi(new { type = "updateProgress", pct })));
                });
                SaveEverything();
                Process.Start(path, "/SILENT /SUPPRESSMSGBOXES /NOCANCEL");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                PostUi(new { type = "toast", text = "Update failed: " + ex.Message, icon = "update" });
            }
        }
    }
}
