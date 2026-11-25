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
        Timer popupWatch;
        bool popupHasFocus, popupEverFocused;

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
            // the popup measures itself and asks Mica to size to its content, like Chrome
            await c.AddScriptToExecuteOnDocumentCreatedAsync(
                "(function(){function send(){try{var d=document.documentElement,b=document.body;if(!b)return;" +
                "var w=Math.max(b.scrollWidth,d.scrollWidth,b.offsetWidth),h=Math.max(b.scrollHeight,d.scrollHeight,b.offsetHeight);" +
                "chrome.webview.postMessage(JSON.stringify({__micaPopup:1,w:w,h:h}));}catch(e){}}" +
                "window.addEventListener('load',function(){send();setTimeout(send,120);setTimeout(send,400);});" +
                "new ResizeObserver(send).observe(document.documentElement);document.addEventListener('DOMContentLoaded',send);})();");
            c.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var m = J.Obj(e.TryGetWebMessageAsString());
                    if (!m.Bool("__micaPopup")) return;
                    SizePopup(anchor, m.Int("w", 360), m.Int("h", 480));
                }
                catch { }
            };
            // Close when focus leaves the popup (clicking the page or anywhere else), like Chrome.
            // Focus bounces around for a moment right after opening — the click that opened the popup is
            // still being handled by the toolbar — so watch the focus state instead of closing on the
            // first LostFocus, and only once the popup has actually held focus.
            var ctl = controllerField?.GetValue(v) as CoreWebView2Controller;
            if (ctl != null)
            {
                ctl.GotFocus += (s, e) => { popupHasFocus = true; popupEverFocused = true; };
                ctl.LostFocus += (s, e) => popupHasFocus = false;
            }
            popupHasFocus = popupEverFocused = false;
            var opened = DateTime.UtcNow;
            popupWatch = new Timer { Interval = 200 };
            popupWatch.Tick += (s, e) =>
            {
                if (extPopup != v) { StopPopupWatch(); return; }
                if (!popupEverFocused || popupHasFocus) return;
                if ((DateTime.UtcNow - opened).TotalMilliseconds < 700) return;
                CloseExtPopup();
            };
            popupWatch.Start();
            SizePopup(anchor, 360, 480);
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

        void SizePopup(Rectangle anchor, int w, int h)
        {
            if (extPopup == null) return;
            double scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96.0;
            w = Math.Min(Math.Max(w + 2, 240), 760);
            h = Math.Min(Math.Max(h + 2, 80), 600);
            var wp = (int)Math.Round(w * scale);
            var hp = (int)Math.Round(h * scale);
            // anchor is already in device pixels (from the UI). Hang the popup under its right edge.
            int x = Math.Min(anchor.Right - wp, ClientSize.Width - wp - 6);
            int y = Math.Min(anchor.Bottom + 4, ClientSize.Height - hp - 6);
            x = Math.Max(6, x); y = Math.Max(6, y);
            extPopup.Bounds = new Rectangle(x, y, wp, hp);
            extPopup.BringToFront();
        }

        void StopPopupWatch()
        {
            if (popupWatch == null) return;
            popupWatch.Stop(); popupWatch.Dispose(); popupWatch = null;
        }

        void CloseExtPopup()
        {
            StopPopupWatch();
            extPopupId = null;
            if (extPopup != null) { Controls.Remove(extPopup); extPopup.Dispose(); extPopup = null; }
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
