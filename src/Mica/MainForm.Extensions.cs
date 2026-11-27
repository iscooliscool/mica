using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Mica
{
    // Extension toolbar buttons, their popups, and the extension side panel.
    partial class MainForm
    {
        WebView2 extView;              // the side panel (ChatGPT, Claude, …)
        bool extOpen;
        string extPanelId;             // which extension owns the open side panel
        Rectangle rectExt;

        WebView2 extPopup;             // a floating action popup, like Chrome
        string extPopupId;
        Rectangle popupAnchor;         // the toolbar icon it hangs from, in client device pixels
        IntPtr popupHook;              // low-level mouse hook, installed only while a popup is open
        Timer popupSizer;              // re-measures the open popup so it follows its content
        Native.LowLevelMouseProc popupHookProc;

        // Send the enabled extensions to the toolbar.
        async Task SendExtToolbar()
        {
            try
            {
                var all = await Extensions.List(Profile);
                // Only extensions with a real button, like Chrome's toolbar — not Google/Microsoft
                // component extensions (PDF viewer, payments, offline docs) that run purely in the background.
                var items = all.Where(o => IsEnabled(o) && HasButton(o)).ToList();
                PostUi(new { type = "extToolbar", list = items, openPanel = extOpen ? extPanelId : null });
            }
            catch { }
        }

        static bool IsEnabled(object o)
        {
            var p = o.GetType().GetProperty("enabled");
            return p != null && (bool)p.GetValue(o);
        }
        static bool HasButton(object o)
        {
            var ha = o.GetType().GetProperty("hasAction");
            bool hasAction = ha != null && (bool)ha.GetValue(o);
            return hasAction || Prop(o, "sidePanel") != null || Prop(o, "popup") != null;
        }
        static string Prop(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o) as string;

        async Task ExtAction(string id, Rectangle anchor)
        {
            DebugLog("ExtAction " + id + " current=" + extPopupId);
            if (extPopupId == id) { CloseExtPopup(); return; }   // clicking the same icon closes it
            CloseExtPopup();
            var list = await Extensions.List(Profile);
            var ext = list.FirstOrDefault(o => Prop(o, "id") == id);
            if (ext == null) return;
            string side = Prop(ext, "sidePanel"), popup = Prop(ext, "popup"), options = Prop(ext, "options");
            if (side != null) ToggleExtPanel(id, side, Prop(ext, "name"));
            else if (popup != null) await OpenExtPopup(id, popup, anchor);
            else if (options != null) _ = NewTab(options);
            else PostUi(new { type = "extNote", id, name = Prop(ext, "name") });   // a small card under the icon, not a toolbar banner
        }

        // ---------------- side panel ----------------
        void ToggleExtPanel(string id, string url, string name)
        {
            if (extOpen && extPanelId == id) { CloseExtPanel(); return; }
            if (aiOpen) ToggleAi(false);
            if (devOpen) CloseDevTools();
            extPanelId = id;
            extOpen = true;
            PostUi(new { type = "extPanel", open = true, id, name });
            if (extView == null) _ = CreateExtPanel(url);
            else extView.CoreWebView2?.Navigate(url);
            ApplyLayout();
            SendState();
        }

        async Task CreateExtPanel(string url)
        {
            var v = new WebView2 { Visible = false, DefaultBackgroundColor = Color.White };
            extView = v;
            Controls.Add(v);
            _ = v.Handle;
            await v.EnsureCoreWebView2Async(env, ControllerOptions());
            if (extView != v) return;
            HookKeys(v);
            var c = v.CoreWebView2;
            c.Settings.IsStatusBarEnabled = false;
            c.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTab(e.Uri); };
            HookDownloads(c);
            c.Navigate(url);
            ApplyLayout();
        }

        void CloseExtPanel()
        {
            extOpen = false;
            extPanelId = null;
            if (extView != null) { Controls.Remove(extView); extView.Dispose(); extView = null; }
            rectExt = Rectangle.Empty;
            PostUi(new { type = "extPanel", open = false });
            ApplyLayout();
            SendState();
        }

        // ---------------- popup ----------------
        async Task OpenExtPopup(string id, string url, Rectangle anchor)
        {
            CloseExtPopup();
            extPopupId = id;
            var v = new WebView2 { Visible = false, DefaultBackgroundColor = Color.White };
            extPopup = v;
            Controls.Add(v);
            _ = v.Handle;
            await v.EnsureCoreWebView2Async(env, ControllerOptions());
            if (extPopup != v) return;
            HookKeys(v);
            var c = v.CoreWebView2;
            c.Settings.IsStatusBarEnabled = false;
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.NewWindowRequested += (s, e) => { e.Handled = true; _ = NewTab(e.Uri); };
            HookDownloads(c);
            // Chrome draws popups without the page's own outer scrollbars and sizes the window to the content
            // instead. Hide the root scrollbars (inner scroll areas are untouched; the wheel still scrolls).
            await c.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){function add(){try{var s=document.createElement('style');" +
                "s.textContent='html::-webkit-scrollbar,body::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}';" +
                "(document.head||document.documentElement).appendChild(s);}catch(e){}}" +
                "if(document.documentElement)add();else document.addEventListener('readystatechange',add,{once:true});})();");
            // Size to the content. Mica measures the page itself (ExecuteScriptAsync works on every
            // extension page; a postMessage from inside the page never reached Mica, so popups were stuck
            // at their starting size). Re-measured while open, so popups that grow after loading follow.
            popupSizer = new Timer { Interval = 250 };
            popupSizer.Tick += async (s, e) => { if (extPopup == v) await MeasurePopup(v); else { ((Timer)s).Stop(); ((Timer)s).Dispose(); } };
            popupSizer.Start();
            c.DOMContentLoaded += async (s, e) => { if (extPopup == v) await MeasurePopup(v); };
            // The extension called window.close() (many popups do this after acting) — close like Chrome.
            c.WindowCloseRequested += (s, e) => BeginInvoke(new Action(() => { if (extPopup == v) CloseExtPopup("window.close"); }));
            // Close on any click outside the popup, like Chrome. This used to watch keyboard focus, but
            // focus bounces between the toolbar and the popup right after the opening click, which made
            // popups close by themselves depending on how fast they loaded. Clicks are unambiguous.
            var ctl = controllerField?.GetValue(v) as CoreWebView2Controller;
            popupAnchor = anchor;
            UpdateMouseHook();
            SizePopup(anchor, 240, 40);   // grows to the content as soon as it's measured
            c.NavigationCompleted += (s, e) =>
            {
                // make sure focus really lands in the popup once it has something to show
                try { if (extPopup == v) { v.BringToFront(); ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); } } catch { }
            };
            c.Navigate(url);
            v.Visible = true;
            v.BringToFront();
            v.Focus();
        }

        // The popup's preferred size in CSS pixels: its width as if nothing constrained it (Chrome lays
        // popups out at their natural width), and its full content height at that width.
        const string MeasureJs =
            "(function(){var d=document.documentElement,b=document.body;if(!d||!b)return null;" +
            "var o=d.style.width;d.style.width='max-content';" +
            "var w=Math.ceil(Math.max(d.getBoundingClientRect().width,b.getBoundingClientRect().width));d.style.width=o;" +
            "var h=Math.ceil(Math.max(d.scrollHeight,b.scrollHeight,b.getBoundingClientRect().height));" +
            "return w+','+h;})()";

        async Task MeasurePopup(WebView2 v)
        {
            try
            {
                var r = await v.CoreWebView2.ExecuteScriptAsync(MeasureJs);
                if (extPopup != v || r == null || r == "null") return;
                var p = r.Trim('"').Split(',');
                int w = int.Parse(p[0]), h = int.Parse(p[1]);
                if (w <= 0 || h <= 0) return;
                SizePopup(popupAnchor, w, h);
            }
            catch { }
        }

        void SizePopup(Rectangle anchor, int w, int h)
        {
            if (extPopup == null) return;
            double scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96.0;
            // Chrome's limits: at least 25 px, at most 800 × 600.
            w = Math.Min(Math.Max(w, 25), 800);
            h = Math.Min(Math.Max(h, 25), 600);
            var wp = (int)Math.Round(w * scale);
            var hp = (int)Math.Round(h * scale);
            // anchor is already in device pixels (from the UI). Hang the popup under its right edge.
            int x = Math.Min(anchor.Right - wp, ClientSize.Width - wp - 6);
            int y = Math.Min(anchor.Bottom + 4, ClientSize.Height - hp - 6);
            x = Math.Max(6, x); y = Math.Max(6, y);
            var b = new Rectangle(x, y, wp, hp);
            if (extPopup.Bounds == b) return;
            extPopup.Bounds = b;
            extPopup.BringToFront();
        }

        // One low-level mouse hook, installed only while something needs "click outside to close":
        // an extension popup, or a live menu (see UpdateUiRegion).
        void UpdateMouseHook()
        {
            bool need = extPopup != null || (overlay && overlayLive);
            if (need && popupHook == IntPtr.Zero)
            {
                popupHookProc = PopupMouseHook;   // keep the delegate alive while the hook is installed
                popupHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, popupHookProc, Native.GetModuleHandle(null), 0);
            }
            else if (!need && popupHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(popupHook);
                popupHook = IntPtr.Zero;
            }
        }

        int swallowUp;   // button-up message to eat after eating its button-down

        // Is the window under this screen point part of this Mica window (pages are WebView2 child windows)?
        bool IsOurWindowAt(Point pt)
        {
            var h = Native.WindowFromPoint(new Native.POINT { x = pt.X, y = pt.Y });
            return h != IntPtr.Zero && Native.GetAncestor(h, 2 /* GA_ROOT */) == Handle;
        }

        IntPtr PopupMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int msg = wParam.ToInt32();
                bool down = msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN;
                if (nCode >= 0 && swallowUp != 0 && msg == swallowUp) { swallowUp = 0; return (IntPtr)1; }
                if (nCode >= 0 && down && overlay && overlayLive && ui?.Region != null)
                {
                    var info = (Native.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    var pt = new Point(info.x, info.y);
                    // A click on the live page (outside the menu and the toolbar) only closes the menu, like
                    // the full-window click catcher used to — it must not also reach the page (it would pause
                    // a video, follow a link, …). Clicks in other apps close the menu and go through.
                    if (!ui.Region.IsVisible(ui.PointToClient(pt)))
                    {
                        BeginInvoke(new Action(() => { if (overlay && overlayLive) PostUi(new { type = "overlayOutside" }); }));
                        if (IsOurWindowAt(pt))
                        {
                            swallowUp = msg + 1;   // the matching button-up
                            return (IntPtr)1;
                        }
                    }
                }
                if (nCode >= 0 && extPopup != null && down)
                {
                    var info = (Native.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    var pt = new Point(info.x, info.y);
                    var popupRect = extPopup.RectangleToScreen(extPopup.ClientRectangle);
                    // A click on the popup's own toolbar icon is left to ExtAction, which toggles it closed.
                    var iconRect = new Rectangle(PointToScreen(popupAnchor.Location), popupAnchor.Size);
                    if (!popupRect.Contains(pt) && !iconRect.Contains(pt))
                    {
                        var v = extPopup;   // only close the popup that was open when the click happened
                        BeginInvoke(new Action(() => { if (extPopup == v) CloseExtPopup("click outside"); }));
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(popupHook, nCode, wParam, lParam);
        }

        void CloseExtPopup() => CloseExtPopup("other");
        void CloseExtPopup(string why)
        {
            if (popupSizer != null) { popupSizer.Stop(); popupSizer.Dispose(); popupSizer = null; }
            extPopupId = null;
            var v = extPopup;
            if (v == null) return;
            DebugLog("CloseExtPopup " + why);
            extPopup = null;            // clear first: removing the control can re-enter this method
            UpdateMouseHook();
            v.Visible = false;
            Controls.Remove(v);
            v.Dispose();
        }

        void LayoutExtras()
        {
            if (extView != null)
            {
                if (extOpen && rectExt.Width > 0) { Place(extView, rectExt); extView.Visible = true; }
                else extView.Visible = false;
            }
            if (extPopup != null) extPopup.BringToFront();
        }
    }
}
