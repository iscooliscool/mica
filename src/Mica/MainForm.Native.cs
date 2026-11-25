using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    // Mica's own versions of the bits WebView2 would otherwise draw in Edge's style:
    // the right-click menu, the find bar and permission prompts. Also hides the "Edg/" tag sites see.
    partial class MainForm
    {
        // ---------------- user agent ----------------
        static void NoEdge(CoreWebView2 c)
        {
            try
            {
                var ua = c.Settings.UserAgent ?? "";
                var clean = Regex.Replace(ua, @"\s*Edg/\S+", "");
                if (clean != ua && clean.Length > 0) c.Settings.UserAgent = clean;
            }
            catch { }
        }

        // ---------------- right-click menu ----------------
        CoreWebView2ContextMenuRequestedEventArgs menuArgs;
        CoreWebView2Deferral menuDeferral;
        // Edge-only extras that don't belong in Mica's menu
        static readonly Regex edgeOnly = new Regex("^(share|webCapture|readAloud|immersiveReader|addToCollection|collections|searchWithBing|bingSearch|createQrCode|sidebar|copilot|visualSearch|translate)", RegexOptions.IgnoreCase);

        static void DebugLog(string s) { try { if (Environment.GetEnvironmentVariable("MICA_DATA") != null) System.IO.File.AppendAllText(System.IO.Path.Combine(Store.Dir, "debug.log"), DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

        class MenuExtra
        {
            public string Label, Icon; public Action Act;
            public MenuExtra(string label, string icon, Action act) { Label = label; Icon = icon; Act = act; }
        }
        const int ExtraBase = 1000000;   // ids for Mica's own items (the engine's ids are small)
        readonly Dictionary<int, Action> menuActions = new Dictionary<int, Action>();

        void ShowMicaMenu(BrowserTab t, CoreWebView2ContextMenuRequestedEventArgs e, List<MenuExtra> extras)
        {
            try { ShowMicaMenuCore(t, e, extras); } catch (Exception ex) { DebugLog("menu: " + ex); }
        }

        void ShowMicaMenuCore(BrowserTab t, CoreWebView2ContextMenuRequestedEventArgs e, List<MenuExtra> extras)
        {
            if (!uiReady || t?.View == null) return;          // fall back to the engine's menu
            FinishMenu(-1);
            menuActions.Clear();
            var list = new List<object>();
            int n = 0;
            foreach (var x in extras)
            {
                int id = ExtraBase + n++;
                menuActions[id] = x.Act;
                list.Add(new { id, name = "mica", icon = x.Icon, label = x.Label, kind = "command", enabled = true, check = false, key = "", children = (object)null });
            }
            if (list.Count > 0) list.Add(new { id = -1, name = "", icon = "", label = "", kind = "separator", enabled = true, check = false, key = "", children = (object)null });
            list.AddRange(MenuJson(e.MenuItems.ToList(), t));
            list = Tidy(list);
            if (list.Count == 0) { e.Handled = true; return; }
            menuArgs = e;
            menuDeferral = e.GetDeferral();
            e.Handled = true;
            var v = t.View;
            PostUi(new { type = "pageMenu", x = v.Left + e.Location.X, y = v.Top + e.Location.Y, items = list });
        }

        List<object> MenuJson(List<CoreWebView2ContextMenuItem> items, BrowserTab t)
        {
            var list = new List<object>();
            foreach (var i in items)
            {
                var name = i.Name ?? "";
                if (i.Kind != CoreWebView2ContextMenuItemKind.Separator && edgeOnly.IsMatch(name)) continue;
                if (name == "inspectElement" && DevToolsPort > 0)
                {
                    // "Inspect" opens Mica's docked DevTools, not a separate Edge window
                    int id = ExtraBase + 500 + menuActions.Count;
                    menuActions[id] = () => _ = OpenDevTools(t);
                    list.Add(new { id, name, icon = "code", label = "Inspect", kind = "command", enabled = true, check = false, key = "F12", children = (object)null });
                    continue;
                }
                list.Add(new
                {
                    id = i.CommandId, name, icon = "",
                    label = i.Label ?? "",
                    kind = i.Kind.ToString().ToLowerInvariant(),
                    enabled = i.IsEnabled,
                    check = i.IsChecked,
                    key = i.ShortcutKeyDescription ?? "",
                    children = i.Kind == CoreWebView2ContextMenuItemKind.Submenu ? (object)Tidy(MenuJson(i.Children.ToList(), t)) : null,
                });
            }
            return list;
        }

        // no doubled-up or dangling separators
        static List<object> Tidy(List<object> list)
        {
            bool IsSep(object o) => (string)o.GetType().GetProperty("kind").GetValue(o) == "separator";
            var outList = new List<object>();
            foreach (var o in list)
            {
                if (IsSep(o) && (outList.Count == 0 || IsSep(outList[outList.Count - 1]))) continue;
                outList.Add(o);
            }
            while (outList.Count > 0 && IsSep(outList[outList.Count - 1])) outList.RemoveAt(outList.Count - 1);
            return outList;
        }

        void FinishMenu(int id)
        {
            var e = menuArgs; var d = menuDeferral;
            menuArgs = null; menuDeferral = null;
            if (e == null) return;
            Action act = null;
            if (id >= ExtraBase) menuActions.TryGetValue(id, out act);
            else try { if (id >= 0) e.SelectedCommandId = id; } catch { }
            try { d?.Complete(); } catch { }
            if (act != null) BeginInvoke(act);
        }

        // ---------------- find in page ----------------
        BrowserTab findTab;

        void HookFind(BrowserTab t, CoreWebView2 c)
        {
            try
            {
                c.Find.MatchCountChanged += (s, e) => { if (t == findTab) SendFind(c); };
                c.Find.ActiveMatchIndexChanged += (s, e) => { if (t == findTab) SendFind(c); };
            }
            catch { }
        }

        void SendFind(CoreWebView2 c)
        {
            try { PostUi(new { type = "findCount", count = c.Find.MatchCount, index = c.Find.ActiveMatchIndex }); } catch { }
        }

        async Task FindStart(string q)
        {
            var t = active; var c = t?.View?.CoreWebView2;
            if (c == null) return;
            if (findTab != null && findTab != t) FindStop();
            findTab = t;
            try
            {
                if (string.IsNullOrEmpty(q)) { c.Find.Stop(); PostUi(new { type = "findCount", count = 0, index = 0 }); return; }
                var o = env.CreateFindOptions();
                o.FindTerm = q;
                o.SuppressDefaultFindDialog = true;
                o.ShouldHighlightAllMatches = true;
                o.IsCaseSensitive = false;
                await c.Find.StartAsync(o);
                SendFind(c);
            }
            catch { }
        }

        void FindStop()
        {
            try { findTab?.View?.CoreWebView2?.Find.Stop(); } catch { }
            findTab = null;
        }

        void OpenFind()
        {
            if (active?.View == null || overlay) return;
            string sel = null;
            PostUi(new { type = "openFind", q = sel });
            ui?.Focus();
        }

        // ---------------- permission prompts ----------------
        class PermAsk { public CoreWebView2PermissionRequestedEventArgs E; public CoreWebView2Deferral D; }
        readonly Queue<PermAsk> permQueue = new Queue<PermAsk>();
        bool permShowing;

        void HookPermissions(BrowserTab t, CoreWebView2 c)
        {
            c.PermissionRequested += (s, e) =>
            {
                var k = e.PermissionKind;
                // these never need asking: let the engine decide as usual
                if (k == CoreWebView2PermissionKind.Autoplay || k == CoreWebView2PermissionKind.UnknownPermission || k == CoreWebView2PermissionKind.PersistentStorage) return;
                if (!uiReady) return;
                try { e.Handled = true; } catch { }
                permQueue.Enqueue(new PermAsk { E = e, D = e.GetDeferral() });
                if (t != active) Activate(t);
                ShowNextPermission();
            };
        }

        void ShowNextPermission()
        {
            if (permShowing || permQueue.Count == 0) return;
            permShowing = true;
            var e = permQueue.Peek().E;
            string host;
            try { host = new Uri(e.Uri).Host; } catch { host = e.Uri; }
            PostUi(new { type = "permission", host, kind = e.PermissionKind.ToString() });
        }

        void AnswerPermission(string answer)
        {
            if (permQueue.Count == 0) { permShowing = false; return; }
            var ask = permQueue.Dequeue(); var e = ask.E; var d = ask.D;
            try
            {
                // "dismiss" (closed without choosing) is a no for now, but asks again next time
                e.State = answer == "allow" ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.SavesInProfile = answer != "dismiss";
            }
            catch { }
            try { d.Complete(); } catch { }
            permShowing = false;
            ShowNextPermission();
        }
    }
}
