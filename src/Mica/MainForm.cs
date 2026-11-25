using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Mica
{
    /// <summary>
    /// The browser window. The whole interface (tabs, toolbar, menus, tour) is one small web page
    /// (ui/chrome.html) that sits behind the tab views; each tab is its own WebView2 placed where
    /// the interface page tells us to put it.
    /// </summary>
    public partial class MainForm : Form
    {
        // Not ".local": Windows does a slow network lookup for .local names (~2 s per page).
        // The interface and Mica's pages are different sites, so they never share a renderer
        // process (a stuck or crashed page can't take the toolbar down with it).
        const string UiHost = "mica-ui.example";
        const string UiOrigin = "https://mica-ui.example/";
        const string PagesHost = "mica.example";
        const string PagesUrl = "https://mica.example/pages/";
        const string WallHost = "mica-wall.example";
        public static int DevToolsPort;
        // After a bulk extension import, extensions may try to open welcome/sign-in tabs: hold those back.
        public static DateTime QuietTabsUntil = DateTime.MinValue;
        static bool QuietTab(CoreWebView2NewWindowRequestedEventArgs e) => !e.IsUserInitiated && DateTime.UtcNow < QuietTabsUntil;

        readonly Store store = Store.Instance;
        static readonly SysInfo sys = SysInfo.Read();
        static Task<CoreWebView2Environment> sharedEnv;
        /// <summary>Private window: nothing is saved, all browsing data is thrown away on close.</summary>
        public readonly bool Incognito;
        readonly string uiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
        readonly List<BrowserTab> tabs = new List<BrowserTab>();
        readonly Stack<string> closedTabs = new Stack<string>();
        readonly string[] startUrls;

        CoreWebView2Environment env;
        WebView2 ui;
        bool uiReady;
        BrowserTab active;
        BrowserTab splitA, splitB;
        WebView2 ai;
        bool aiOpen;
        Rectangle rectA, rectB, rectAi;
        int contentRadius;
        bool overlay;
        bool fullscreen;
        BrowserTab fsTab;
        Rectangle preFsBounds;
        bool preFsMax;
        string pipScript = "";
        string storeScript = "";
        WebView2 spare;           // a new-tab page loaded ahead of time so Ctrl+T is instant
        bool spareBusy;
        Color pageBg = Color.White;
        bool stateQueued;
        Updater.Info pendingUpdate;

        readonly Timer sleepTimer = new Timer { Interval = 15000 };
        readonly Timer memTimer = new Timer { Interval = 2500 };
        int ticks;

        public MainForm(string[] urls, bool incognito = false)
        {
            startUrls = urls ?? new string[0];
            Incognito = incognito;
            Text = incognito ? "Mica (Private)" : "Mica";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(238, 240, 245);
            MinimumSize = new Size(520, 360);
            StartPosition = FormStartPosition.Manual;
            RestoreWindowBounds();
            Load += async (s, e) => await InitAsync();
            FormClosing += (s, e) => SaveEverything();
            Resize += (s, e) => { SendState(); };
            sleepTimer.Tick += (s, e) => SleepTick();
            memTimer.Tick += (s, e) => SendMemory();
            InitDownloads();
        }

        // =====================================================================
        //  Startup
        // =====================================================================
        async Task InitAsync()
        {
            if (sharedEnv == null)
            {
                // Potato mode: Chromium's own low-memory mode + fewer page processes.
                string args = store.B("potato")
                    ? "--enable-low-end-device-mode --renderer-process-limit=2 --disable-smooth-scrolling"
                    : "";
                // Docked Chrome DevTools talk to the engine over a local-only debugging port.
                if (store.Settings.Bool("devtoolsPanel", true))
                {
                    DevToolsPort = int.TryParse(Environment.GetEnvironmentVariable("MICA_DEVTOOLS_PORT"), out var fixedPort) ? fixedPort : PortIfFree(store.Settings.Int("devtoolsPort")) ?? FreePort();
                    if (DevToolsPort > 0) store.Set("devtoolsPort", DevToolsPort);
                    if (DevToolsPort > 0)
                        args += " --remote-debugging-address=127.0.0.1 --remote-debugging-port=" + DevToolsPort +
                                " --remote-allow-origins=http://127.0.0.1:" + DevToolsPort;
                }
                var opts = new CoreWebView2EnvironmentOptions(args.Trim()) { AreBrowserExtensionsEnabled = true, EnableTrackingPrevention = true };
                sharedEnv = CoreWebView2Environment.CreateAsync(null, Store.ProfileDir, opts);
            }
            env = await sharedEnv;
            try { pipScript = File.ReadAllText(Path.Combine(uiDir, "inject", "pip.js")); } catch { }
            try { storeScript = File.ReadAllText(Path.Combine(uiDir, "inject", "webstore.js")); } catch { }

            ui = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(ui);
            await ui.EnsureCoreWebView2Async(env);
            HookKeys(ui);
            var c = ui.CoreWebView2;
            c.SetVirtualHostNameToFolderMapping(UiHost, uiDir, CoreWebView2HostResourceAccessKind.Allow);
            var st = c.Settings;
            bool dev = Environment.GetEnvironmentVariable("MICA_DEV") == "1";
            st.AreDevToolsEnabled = dev;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            c.WebMessageReceived += (s, e) => { if (e.Source.StartsWith(UiOrigin)) OnUiMessage(J.Obj(e.WebMessageAsJson)); };
            c.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith(UiOrigin)) { e.Cancel = true; _ = NewTab(e.Uri); } };
            c.NewWindowRequested += (s, e) => { e.Handled = true; if (!QuietTab(e)) _ = NewTab(e.Uri); };
            // If the interface's renderer ever crashes or hangs, bring it straight back.
            c.ProcessFailed += (s, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
                BeginInvoke(new Action(() => { try { uiReady = false; ui.CoreWebView2.Reload(); } catch { } }));
            };
            c.ContextMenuRequested += (s, e) =>
            {
                if (!e.ContextMenuTarget.IsEditable) { e.Handled = true; return; }
                var keep = new HashSet<string> { "cut", "copy", "paste", "selectAll", "undo", "redo", "emoji" };
                foreach (var item in e.MenuItems.ToList())
                    if (item.Kind != CoreWebView2ContextMenuItemKind.Separator && !keep.Contains(item.Name)) e.MenuItems.Remove(item);
            };
            ApplyTracking();
            // After an update, drop cached copies of the old interface files.
            if (store.S("lastVersion") != Updater.CurrentText)
            {
                try { await c.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache); } catch { }
                store.Set("lastVersion", Updater.CurrentText);
            }
            c.Navigate(UiOrigin + "chrome.html");

            // Tabs: restore the last session (asleep, so they cost nothing) plus anything we were launched with.
            if (store.B("restoreSession") && !Incognito) RestoreSession();
            foreach (var u in startUrls) await NewTab(Resolve(u) ?? "mica://newtab");
            if (tabs.Count == 0) await NewTab("mica://newtab");
            else if (active == null) Activate(tabs.Last());

            sleepTimer.Start();
            if (store.B("showMemory")) memTimer.Start();
            _ = Extensions.LoadAll(env, store);
        }

        static int? PortIfFree(int port)
        {
            if (port <= 1024) return null;
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                l.Start(); l.Stop();
                return port;
            }
            catch { return null; }
        }

        static int FreePort()
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                l.Start();
                int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }
            catch { return 0; }
        }

        /// <summary>Mica's own pages get their settings baked in, so they draw instantly (no round trip).</summary>
        string BootScript() =>
            "if(location.host==='" + PagesHost + "'){window.__micaBoot=" + J.Ser(new { settings = store.Settings, sys = SysObject(), bookmarks = store.Bookmarks, incognito = Incognito }) + ";}";

        async Task ApplyBootScript(WebView2 v, BrowserTab t)
        {
            var c = v?.CoreWebView2;
            if (c == null) return;
            var old = t != null ? t.BootScriptId : v.AccessibleDescription;
            if (old != null) try { c.RemoveScriptToExecuteOnDocumentCreated(old); } catch { }
            var id = await c.AddScriptToExecuteOnDocumentCreatedAsync(BootScript());
            if (t != null) t.BootScriptId = id; else v.AccessibleDescription = id;
        }

        void RefreshBootScripts()
        {
            foreach (var t in tabs) if (t.View?.CoreWebView2 != null) _ = ApplyBootScript(t.View, t);
            if (spare?.CoreWebView2 != null) _ = ApplyBootScript(spare, null);
        }

        bool LowMemoryPc => sys.RamGB <= 3.2 || store.B("potato");

        /// <summary>Keeps one new-tab page ready in the background (skipped on low-memory PCs).</summary>
        async Task PrepareSpare()
        {
            if (spare != null || spareBusy || LowMemoryPc || env == null) return;
            spareBusy = true;
            try
            {
                await Task.Delay(1200);
                var v = new WebView2 { Visible = false, DefaultBackgroundColor = pageBg };
                Controls.Add(v);
                _ = v.Handle;
                await v.EnsureCoreWebView2Async(env, ControllerOptions());
                SetupPageView(v.CoreWebView2);
                await ApplyBootScript(v, null);
                if (pipScript.Length > 0 && store.B("pipButton")) v.Tag = await v.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(pipScript);
                v.CoreWebView2.Navigate(ToNavigable("mica://newtab"));
                spare = v;
            }
            catch { }
            finally { spareBusy = false; }
        }

        void SetupPageView(CoreWebView2 c)
        {
            c.SetVirtualHostNameToFolderMapping(PagesHost, uiDir, CoreWebView2HostResourceAccessKind.Allow);
            c.SetVirtualHostNameToFolderMapping(WallHost, Store.WallDir, CoreWebView2HostResourceAccessKind.DenyCors);
            c.Settings.AreDevToolsEnabled = true;
            c.Settings.IsStatusBarEnabled = true;
            c.Settings.IsPasswordAutosaveEnabled = true;
            c.Settings.IsGeneralAutofillEnabled = true;
        }

        void ApplyTracking()
        {
            try
            {
                var lvl = store.S("tracking");
                ui.CoreWebView2.Profile.PreferredTrackingPreventionLevel =
                    lvl == "strict" ? CoreWebView2TrackingPreventionLevel.Strict :
                    lvl == "basic" ? CoreWebView2TrackingPreventionLevel.Basic : CoreWebView2TrackingPreventionLevel.Balanced;
            }
            catch { }
        }

        public void OpenFromOtherInstance(string[] urls)
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Native.SetForegroundWindow(Handle);
            if (urls.Length == 0) { _ = NewTab("mica://newtab"); return; }
            foreach (var u in urls) _ = NewTab(Resolve(u) ?? "mica://newtab");
        }

        // =====================================================================
        //  Custom window frame (no Windows title bar; Windows still gives us resize borders + snapping)
        // =====================================================================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                if (fullscreen) { m.Result = IntPtr.Zero; return; }
                var p = (Native.NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(Native.NCCALCSIZE_PARAMS));
                int origTop = p.r0.top, origLeft = p.r0.left;
                base.WndProc(ref m);
                p = (Native.NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(Native.NCCALCSIZE_PARAMS));
                // Keep Windows' side/bottom borders (they're invisible and give resizing + shadow), remove the caption.
                p.r0.top = Native.IsZoomed(Handle) ? origTop + (p.r0.left - origLeft) : origTop;
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void StartDrag(int hit)
        {
            if (fullscreen) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        }

        void RestoreWindowBounds()
        {
            var w = Incognito ? null : store.Settings.Sub("window");
            var wa = Screen.PrimaryScreen.WorkingArea;
            Rectangle r;
            if (w != null)
            {
                r = new Rectangle(w.Int("x"), w.Int("y"), Math.Max(520, w.Int("w")), Math.Max(360, w.Int("h")));
                if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) r = Rectangle.Empty;
            }
            else r = Rectangle.Empty;
            if (r.IsEmpty)
            {
                int ww = (int)(wa.Width * 0.82), hh = (int)(wa.Height * 0.86);
                r = new Rectangle(wa.X + (wa.Width - ww) / 2, wa.Y + (wa.Height - hh) / 2, ww, hh);
            }
            Bounds = r;
            if (Incognito) Location = new Point(r.X + 40, r.Y + 40);
            else if (w != null && w.Bool("max") || wa.Width <= 1400) WindowState = FormWindowState.Maximized;
        }

        // =====================================================================
        //  Tabs
        // =====================================================================
        BrowserTab Find(int id) => tabs.FirstOrDefault(t => t.Id == id);

        async Task<BrowserTab> NewTab(string url, bool activate = true, BrowserTab after = null)
        {
            var t = new BrowserTab { Url = url ?? "mica://newtab" };
            if (t.Url == "mica://newtab") t.Title = "New tab";
            int idx = after != null ? tabs.IndexOf(after) + 1 : (active != null ? tabs.IndexOf(active) + 1 : tabs.Count);
            tabs.Insert(Math.Max(0, Math.Min(idx, tabs.Count)), t);
            if (activate) Activate(t); else { await Wake(t); SendState(); }
            return t;
        }

        /// <summary>Creates the tab's web view (wakes a sleeping tab).</summary>
        async Task Wake(BrowserTab t, CoreWebView2NewWindowRequestedEventArgs nw = null, CoreWebView2Deferral deferral = null)
        {
            if (t.View != null || t.Waking) { deferral?.Complete(); return; }
            t.Waking = true;

            // Fast path: a new tab takes the new-tab page that's already loaded.
            if (nw == null && t.Url == "mica://newtab" && spare?.CoreWebView2 != null)
            {
                var sv = spare;
                spare = null;
                t.Waking = false;
                t.View = sv;
                t.BootScriptId = sv.AccessibleDescription;
                t.PipScriptId = sv.Tag as string;
                sv.Tag = null;
                Hook(t, sv.CoreWebView2);
                HookKeys(sv);
                await AddStoreScript(sv.CoreWebView2);
                ApplyLayout();
                SendState();
                _ = PrepareSpare();
                return;
            }

            bool internalPage = t.Url.StartsWith("mica://");
            var v = new WebView2 { Visible = false, DefaultBackgroundColor = internalPage ? pageBg : Color.White };
            Controls.Add(v);
            _ = v.Handle;
            try { await v.EnsureCoreWebView2Async(env, ControllerOptions()); }
            catch { t.Waking = false; v.Dispose(); deferral?.Complete(); return; }
            t.Waking = false;
            if (!tabs.Contains(t)) { v.Dispose(); deferral?.Complete(); return; }
            t.View = v;
            t.Suspended = false;
            var c = v.CoreWebView2;
            SetupPageView(c);
            Hook(t, c);
            HookKeys(v);
            await ApplyBootScript(v, t);
            await AddStoreScript(c);
            if (store.B("pipButton") && pipScript.Length > 0)
                t.PipScriptId = await c.AddScriptToExecuteOnDocumentCreatedAsync(pipScript);

            if (nw != null) { nw.NewWindow = c; nw.Handled = true; deferral?.Complete(); }
            else c.Navigate(ToNavigable(t.Url));
            ApplyLayout();
            SendState();
            _ = PrepareSpare();
        }

        async Task AddStoreScript(CoreWebView2 c)
        {
            if (storeScript.Length > 0) try { await c.AddScriptToExecuteOnDocumentCreatedAsync(storeScript); } catch { }
        }

        /// <summary>Private windows get an InPrivate profile: no cookies/cache/history survive.</summary>
        CoreWebView2ControllerOptions ControllerOptions()
        {
            if (!Incognito) return null;
            var o = env.CreateCoreWebView2ControllerOptions();
            o.IsInPrivateModeEnabled = true;
            o.ProfileName = "Private";
            return o;
        }

        void Hook(BrowserTab t, CoreWebView2 c)
        {
            c.SourceChanged += (s, e) => { t.Url = Display(c.Source); SendState(); };
            c.DocumentTitleChanged += (s, e) =>
            {
                t.Title = string.IsNullOrWhiteSpace(c.DocumentTitle) ? Display(c.Source) : c.DocumentTitle;
                if (!Incognito) store.UpdateHistoryTitle(c.Source, c.DocumentTitle);
                if (t == active) Text = t.Title + (Incognito ? " - Mica (Private)" : " - Mica");
                SendState();
            };
            c.FaviconChanged += (s, e) => { t.Favicon = c.FaviconUri ?? ""; SendState(); };
            c.NavigationStarting += (s, e) =>
            {
                if (e.Uri.StartsWith("mica://")) { e.Cancel = true; c.Navigate(ToNavigable(e.Uri)); return; }
                t.Loading = true;
                SendState();
            };
            c.NavigationCompleted += (s, e) =>
            {
                t.Loading = false;
                if (e.IsSuccess && !Incognito) store.AddHistory(c.Source, c.DocumentTitle);
                SendState();
            };
            c.HistoryChanged += (s, e) => SendState();
            c.NewWindowRequested += async (s, e) =>
            {
                if (QuietTab(e)) { e.Handled = true; return; }
                var d = e.GetDeferral();
                var nt = new BrowserTab { Url = e.Uri, Title = "Loading…" };
                tabs.Insert(tabs.IndexOf(t) + 1, nt);
                await Wake(nt, e, d);
                Activate(nt);
            };
            c.IsDocumentPlayingAudioChanged += (s, e) => { t.Audible = c.IsDocumentPlayingAudio; SendState(); };
            c.IsMutedChanged += (s, e) => { t.Muted = c.IsMuted; SendState(); };
            c.ContainsFullScreenElementChanged += (s, e) => SetFullscreen(t, c.ContainsFullScreenElement);
            c.ContextMenuRequested += (s, e) => PageContextMenu(t, e);
            HookDownloads(c);
            NoEdge(c);
            HookFind(t, c);
            HookPermissions(t, c);
            c.WebMessageReceived += (s, e) =>
            {
                if (e.Source.StartsWith(PagesUrl)) OnPageMessage(t, J.Obj(e.WebMessageAsJson));
                else if (e.Source.StartsWith("https://chromewebstore.google.com/")) OnWebStoreMessage(t, J.Obj(e.WebMessageAsJson));
            };
            c.ProcessFailed += (s, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited || e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    BeginInvoke(new Action(() => { t.Loading = false; SendState(); }));
            };
            t.View.GotFocus += (s, e) =>
            {
                if (splitA != null && (t == splitA || t == splitB) && active != t) { active = t; SendState(); }
            };
        }

        void Activate(BrowserTab t)
        {
            if (t == null) return;
            if (splitA != null && t != splitA && t != splitB) { splitA = splitB = null; }
            active = t;
            t.LastActive = DateTime.Now;
            if (t.View == null) _ = Wake(t);
            else
            {
                try { t.View.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
                t.Suspended = false;
            }
            Text = t.Title + (Incognito ? " - Mica (Private)" : " - Mica");
            ApplyLayout();
            SendState();
            if (devOpen && devTab != t && t.View != null) _ = OpenDevTools(t);
            if (findTab != null && findTab != t) { FindStop(); PostUi(new { type = "closeFind" }); }
            if (t.Url == "mica://newtab") { PostUi(new { type = "focusAddress" }); ui?.Focus(); }
            else if (!overlay) t.View?.Focus();
        }

        void CloseTab(BrowserTab t)
        {
            if (t == null) return;
            int idx = tabs.IndexOf(t);
            if (t.Url != "mica://newtab") closedTabs.Push(t.Url);
            if (t == splitA || t == splitB) splitA = splitB = null;
            tabs.Remove(t);
            DisposeView(t);
            if (tabs.Count == 0) { Close(); return; }
            if (t == active) Activate(tabs[Math.Min(idx, tabs.Count - 1)]);
            else { ApplyLayout(); SendState(); }
        }

        void DisposeView(BrowserTab t)
        {
            if (t.View == null) return;
            var v = t.View;
            t.View = null;
            t.PipScriptId = null;
            t.Loading = false;
            Controls.Remove(v);
            v.Dispose();
        }

        /// <summary>Puts a tab to sleep: throws away its page (frees all its memory) but keeps its place.</summary>
        void Sleep(BrowserTab t)
        {
            if (t.View == null || t == active || t == splitA || t == splitB) return;
            DisposeView(t);
            t.Audible = false;
            SendState();
        }

        void SleepTick()
        {
            ticks++;
            double mins = store.N("sleepMinutes", 5);
            foreach (var t in tabs.ToList())
            {
                if (t.View == null || t == active || t == splitA || t == splitB || t.Audible) continue;
                if (t.Url.StartsWith("mica://")) continue; // Mica's own pages are tiny; keep them instant
                if (mins > 0 && (DateTime.Now - t.LastActive).TotalMinutes >= mins) { Sleep(t); continue; }
                if (!t.Suspended && (DateTime.Now - t.HiddenSince).TotalSeconds > 30)
                {
                    t.Suspended = true;
                    try { _ = t.View.CoreWebView2.TrySuspendAsync(); } catch { }
                }
            }
            if (ticks % 4 == 0) SaveSession();
        }

        void Cycle(int dir)
        {
            if (tabs.Count < 2 || active == null) return;
            int i = (tabs.IndexOf(active) + dir + tabs.Count) % tabs.Count;
            Activate(tabs[i]);
        }

        void ReopenClosed()
        {
            if (closedTabs.Count > 0) _ = NewTab(closedTabs.Pop());
        }

        // =====================================================================
        //  Layout: the UI page tells us where the page area(s) are
        // =====================================================================
        void ApplyLayout()
        {
            if (fullscreen || ui == null) return;
            foreach (var t in tabs)
            {
                if (t.View == null) continue;
                Rectangle r = Rectangle.Empty;
                if (splitA != null) { if (t == splitA) r = rectA; else if (t == splitB) r = rectB; }
                else if (t == active) r = rectA;

                if (r.Width > 0 && r.Height > 0)
                {
                    Place(t.View, r);
                    if (!t.View.Visible) t.View.Visible = true;
                }
                else if (t.View.Visible)
                {
                    t.View.Visible = false;
                    t.HiddenSince = DateTime.Now;
                    try { t.View.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; } catch { }
                }
            }
            if (ai != null)
            {
                if (aiOpen && rectAi.Width > 0) { Place(ai, rectAi); ai.Visible = true; }
                else ai.Visible = false;
            }
            if (dev != null)
            {
                if (devOpen && rectDev.Width > 0) { if (dev.Bounds != rectDev) dev.Bounds = rectDev; dev.Visible = true; }
                else dev.Visible = false;
            }
            if (overlay) ui.BringToFront(); else ui.SendToBack();
        }

        void Place(Control v, Rectangle r)
        {
            bool sizeChanged = v.Size != r.Size || (int)(v.Tag ?? -1) != contentRadius;
            if (v.Bounds != r) v.Bounds = r;
            if (sizeChanged)
            {
                v.Tag = contentRadius;
                v.Region = contentRadius > 0 ? Rounded(r.Size, contentRadius) : null;
            }
        }

        static Region Rounded(Size s, int r)
        {
            int d = Math.Min(r * 2, Math.Min(s.Width, s.Height));
            if (d <= 1) return null;
            var p = new GraphicsPath();
            p.AddArc(0, 0, d, d, 180, 90);
            p.AddArc(s.Width - d, 0, d, d, 270, 90);
            p.AddArc(s.Width - d, s.Height - d, d, d, 0, 90);
            p.AddArc(0, s.Height - d, d, d, 90, 90);
            p.CloseFigure();
            return new Region(p);
        }

        static Rectangle Rect(Dictionary<string, object> r)
            => r == null ? Rectangle.Empty : new Rectangle(r.Int("x"), r.Int("y"), Math.Max(0, r.Int("w")), Math.Max(0, r.Int("h")));

        // =====================================================================
        //  Fullscreen (videos, F11)
        // =====================================================================
        void SetFullscreen(BrowserTab t, bool on)
        {
            if (on == fullscreen || t?.View == null) return;
            if (on)
            {
                fsTab = t;
                preFsMax = WindowState == FormWindowState.Maximized;
                if (preFsMax) WindowState = FormWindowState.Normal;
                preFsBounds = Bounds;
                fullscreen = true;
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
                Bounds = Screen.FromControl(this).Bounds;
                t.View.Region = null;
                t.View.Tag = -1;
                t.View.Bounds = new Rectangle(Point.Empty, ClientSize);
                t.View.BringToFront();
            }
            else
            {
                fullscreen = false;
                Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_FRAMECHANGED | Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
                Bounds = preFsBounds;
                if (preFsMax) WindowState = FormWindowState.Maximized;
                fsTab = null;
                ApplyLayout();
            }
            SendState();
        }

        // =====================================================================
        //  Address bar helpers
        // =====================================================================
        string SearchUrl(string q)
        {
            string e = store.S("search");
            string b = e == "duckduckgo" ? "https://duckduckgo.com/?q=" :
                       e == "bing" ? "https://www.bing.com/search?q=" :
                       e == "brave" ? "https://search.brave.com/search?q=" : "https://www.google.com/search?q=";
            return b + Uri.EscapeDataString(q);
        }

        string Resolve(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return null;
            if (q.StartsWith("mica://", StringComparison.OrdinalIgnoreCase)) return q.ToLowerInvariant();
            if (Regex.IsMatch(q, @"^[a-zA-Z]:\\") || q.StartsWith("\\\\"))
                try { return new Uri(q).AbsoluteUri; } catch { }
            if (Regex.IsMatch(q, @"^(https?|file|ftp|edge|about|view-source|data):", RegexOptions.IgnoreCase)) return q;
            if (!q.Contains(" "))
            {
                if (Regex.IsMatch(q, @"^(localhost|\d{1,3}(\.\d{1,3}){3}|\[[0-9a-fA-F:]+\])(:\d+)?([/?#].*)?$")) return "http://" + q;
                if (Regex.IsMatch(q, @"^[^\s/?#]+\.[a-zA-Z][a-zA-Z0-9-]{1,}(:\d+)?([/?#].*)?$")) return "https://" + q;
            }
            return SearchUrl(q);
        }

        /// <summary>mica://settings → the real file URL served from the ui folder.</summary>
        static string ToNavigable(string u)
        {
            if (!u.StartsWith("mica://")) return u;
            var n = u.Substring(7).TrimEnd('/');
            string hash = "";
            int i = n.IndexOf('#');
            if (i >= 0) { hash = n.Substring(i); n = n.Substring(0, i); }
            switch (n)
            {
                case "extensions": return PagesUrl + "extensions.html";
                case "downloads": return PagesUrl + "downloads.html";
                case "history": return PagesUrl + "library.html#history";
                case "bookmarks": return PagesUrl + "library.html#bookmarks";
                case "settings": return PagesUrl + "settings.html" + hash;
                default: return PagesUrl + "newtab.html";
            }
        }

        /// <summary>The real URL → what we show in the address bar.</summary>
        static string Display(string u)
        {
            if (string.IsNullOrEmpty(u) || !u.StartsWith(PagesUrl)) return u ?? "";
            try
            {
                var uri = new Uri(u);
                var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                if (name == "library") return uri.Fragment == "#bookmarks" ? "mica://bookmarks" : "mica://history";
                if (name == "settings") return "mica://settings";
                if (name == "extensions") return "mica://extensions";
                return "mica://" + name;
            }
            catch { return u; }
        }

        // =====================================================================
        //  Session + saving
        // =====================================================================
        void SaveSession()
        {
            if (Incognito) return;
            try
            {
                var list = tabs.Select(t => new Dictionary<string, object> { ["url"] = t.Url, ["title"] = t.Title, ["favicon"] = t.Favicon }).ToList();
                Store.Write("session.json", new Dictionary<string, object> { ["tabs"] = list, ["active"] = active != null ? tabs.IndexOf(active) : 0 });
            }
            catch { }
        }

        void RestoreSession()
        {
            var s = Store.Read("session.json");
            if (s == null || !(s.TryGetValue("tabs", out var o) && o is object[] arr)) return;
            foreach (var d in arr.OfType<Dictionary<string, object>>())
            {
                var url = d.Str("url", "");
                if (url.Length == 0) continue;
                tabs.Add(new BrowserTab { Url = url, Title = d.Str("title", url), Favicon = d.Str("favicon", "") });
            }
            if (tabs.Count > 0 && startUrls.Length == 0)
                Activate(tabs[Math.Max(0, Math.Min(s.Int("active"), tabs.Count - 1))]);
        }

        void SaveEverything()
        {
            if (Incognito) { store.Flush(); return; }
            SaveSession();
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (fullscreen) b = preFsBounds;
            store.Settings["window"] = new Dictionary<string, object> { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height, ["max"] = WindowState == FormWindowState.Maximized || preFsMax && fullscreen };
            store.MarkDirty("settings");
            store.Flush();
        }

        // =====================================================================
        //  Talking to the UI page
        // =====================================================================
        void PostUi(object msg)
        {
            if (!uiReady || ui?.CoreWebView2 == null) return;
            try { ui.CoreWebView2.PostWebMessageAsJson(J.Ser(msg)); } catch { }
        }

        void Post(BrowserTab t, object msg)
        {
            try { t?.View?.CoreWebView2?.PostWebMessageAsJson(J.Ser(msg)); } catch { }
        }

        /// <summary>Sends the tab list etc. to the UI (batched so many changes = one message).</summary>
        void SendState()
        {
            if (stateQueued || !IsHandleCreated) return;
            stateQueued = true;
            BeginInvoke(new Action(() =>
            {
                stateQueued = false;
                PostUi(new
                {
                    type = "state",
                    tabs = tabs.Select(t =>
                    {
                        var cw = t.View?.CoreWebView2;
                        return new
                        {
                            id = t.Id,
                            title = t.Title,
                            url = t.Url,
                            favicon = t.Favicon,
                            loading = t.Loading,
                            asleep = t.Asleep,
                            audible = t.Audible,
                            muted = t.Muted,
                            canBack = cw != null && cw.CanGoBack,
                            canForward = cw != null && cw.CanGoForward,
                            bookmarked = store.IsBookmarked(BookmarkKey(t)),
                        };
                    }).ToList(),
                    activeId = active?.Id ?? 0,
                    split = splitA != null ? new { a = splitA.Id, b = splitB.Id } : null,
                    ai = aiOpen,
                    devtools = devOpen,
                    maximized = WindowState == FormWindowState.Maximized,
                    fullscreen,
                    incognito = Incognito,
                    update = pendingUpdate?.Version,
                });
            }));
        }

        void SendSettings()
        {
            RefreshBootScripts();
            var msg = new { type = "settings", settings = store.Settings };
            PostUi(msg);
            foreach (var t in tabs) if (t.View != null && t.Url.StartsWith("mica://")) Post(t, msg);
        }

        void SendBookmarks()
        {
            var msg = new { type = "bookmarks", list = store.Bookmarks };
            PostUi(msg);
            foreach (var t in tabs) if (t.View != null && t.Url.StartsWith("mica://")) Post(t, msg);
        }

        object SysObject() => new
        {
            ramGB = sys.RamGB,
            cores = sys.Cores,
            cpu = sys.Cpu,
            slow = sys.Slow,
            recommendSleep = sys.RecommendSleep,
            hasChrome = sys.HasChrome,
            hasEdge = sys.HasEdge,
            is64 = sys.Is64Bit,
            version = Updater.CurrentText,
            repo = Updater.RepoUrl,
            engine = env?.BrowserVersionString ?? "",
            incognito = Incognito,
        };

        void SendMemory()
        {
            if (env == null) return;
            try
            {
                long total = Native.PrivateWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Id);
                int renderers = 0;
                long browser = 0, pages = 0, gpu = 0, other = 0;
                foreach (var p in env.GetProcessInfos())
                {
                    long b = Native.PrivateWorkingSet(p.ProcessId);
                    total += b;
                    switch (p.Kind)
                    {
                        case CoreWebView2ProcessKind.Browser: browser += b; break;
                        case CoreWebView2ProcessKind.Renderer: pages += b; renderers++; break;
                        case CoreWebView2ProcessKind.Gpu: gpu += b; break;
                        default: other += b; break;
                    }
                }
                var msg = new
                {
                    type = "memory",
                    mb = total / 1048576,
                    browserMb = browser / 1048576,
                    pagesMb = pages / 1048576,
                    gpuMb = gpu / 1048576,
                    otherMb = other / 1048576,
                    renderers,
                    awake = tabs.Count(t => t.View != null),
                    asleep = tabs.Count(t => t.View == null),
                    ramGB = sys.RamGB,
                };
                PostUi(msg);
                foreach (var t in tabs) if (t.View != null && t.Url == "mica://settings") Post(t, msg);
            }
            catch { }
        }
    }
}
