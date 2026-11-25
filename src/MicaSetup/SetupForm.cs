using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MicaSetup
{
    /// <summary>The installer window: borderless, with its whole look drawn by ui/setup.html.</summary>
    class SetupForm : Form
    {
        public static string TempRoot;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        WebView2 web;
        string dir;
        bool busy, finished;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public SetupForm()
        {
            Text = Program.Uninstall ? "Uninstall Mica" : "Mica Setup";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Program.Uninstall ? Color.FromArgb(28, 26, 44) : Color.FromArgb(96, 128, 246);
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            // Fit (and center) on small screens too, e.g. 1024x768 or 1366x768 laptops.
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = Math.Min((int)(880 * scale), wa.Width - 24), h = Math.Min((int)(560 * scale), wa.Height - 24);
            ClientSize = new Size(w, h);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            dir = Program.TargetDir ?? Installer.InstalledDir() ?? Installer.DefaultDir;
            Load += async (s, e) => await InitAsync();
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;  // drop shadow
                cp.Style |= 0x20000;       // minimize from the taskbar
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // Windows 11 rounded corners
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        async Task InitAsync()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "MicaSetup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var uiDir = Path.Combine(TempRoot, "ui");
            foreach (var name in Program.ResourceNames("ui/"))
            {
                var target = Path.Combine(uiDir, name.Substring(3).Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, Program.ReadResource(name));
            }
            string arch = IntPtr.Size == 4 ? "x86"
                : string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"), "ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64" : "x64";
            File.WriteAllBytes(Path.Combine(TempRoot, "WebView2Loader.dll"), Program.ReadResource("loader." + arch + ".dll"));
            CoreWebView2Environment.SetLoaderDllFolderPath(TempRoot);

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(TempRoot, "data"));
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
            await web.EnsureCoreWebView2Async(env);
            var c = web.CoreWebView2;
            var st = c.Settings;
            st.AreDevToolsEnabled = Environment.GetEnvironmentVariable("MICA_DEV") == "1";
            st.AreDefaultContextMenusEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsStatusBarEnabled = false;
            st.IsSwipeNavigationEnabled = false;
            c.SetVirtualHostNameToFolderMapping("mica-setup.example", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            c.NavigationStarting += (s, e) => { if (!e.Uri.StartsWith("https://mica-setup.example/")) e.Cancel = true; };
            c.NewWindowRequested += (s, e) => e.Handled = true;
            c.WebMessageReceived += (s, e) => OnMessage(Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson));
            c.Navigate("https://mica-setup.example/setup.html");
        }

        void Post(object m)
        {
            try { web?.CoreWebView2?.PostWebMessageAsJson(Json.Serialize(m)); } catch { }
        }

        void OnMessage(Dictionary<string, object> m)
        {
            m.TryGetValue("cmd", out var cmdObj);
            switch (cmdObj as string)
            {
                case "hello":
                    Post(new
                    {
                        type = "info",
                        mode = Program.Uninstall ? "uninstall" : "install",
                        version = Installer.Version,
                        installed = Installer.InstalledVersion(),
                        dir,
                        desktop = Installer.HadDesktopShortcut(),
                    });
                    break;
                case "install":
                    {
                        bool desktop = m.TryGetValue("desktop", out var d) && d is bool b && b;
                        Run(p => Installer.Install(dir, desktop, p));
                        break;
                    }
                case "uninstall":
                    {
                        bool data = m.TryGetValue("deleteData", out var d) && d is bool b && b;
                        Run(p => Installer.Uninstall(dir, data, p));
                        break;
                    }
                case "browse":
                    using (var f = new FolderBrowserDialog { Description = "Choose where to install Mica", SelectedPath = Path.GetDirectoryName(dir), ShowNewFolderButton = true })
                    {
                        if (f.ShowDialog(this) == DialogResult.OK)
                        {
                            dir = f.SelectedPath.TrimEnd('\\').EndsWith("Mica", StringComparison.OrdinalIgnoreCase) ? f.SelectedPath : Path.Combine(f.SelectedPath, "Mica");
                            Post(new { type = "dir", dir });
                        }
                    }
                    break;
                case "launch":
                    try { Installer.Launch(dir); } catch { }
                    Close();
                    break;
                case "close": if (!busy) Close(); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                    break;
                case "github":
                    try { Process.Start(Installer.Repo); } catch { }
                    break;
                case "license":
                    try { Process.Start(Installer.Repo + "/blob/main/LICENSE"); } catch { }
                    break;
            }
        }

        void Run(Action<Action<int, string>> work)
        {
            if (busy || finished) return;
            busy = true;
            int last = -1;
            Action<int, string> progress = (pct, text) =>
            {
                if (pct == last) return;
                last = pct;
                BeginInvoke(new Action(() => Post(new { type = "progress", pct, text })));
            };
            Task.Run(() =>
            {
                try
                {
                    work(progress);
                    BeginInvoke(new Action(() => { busy = false; finished = true; Post(new { type = "done" }); }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => { busy = false; Post(new { type = "error", message = ex.Message }); }));
                }
            });
        }
    }

    /// <summary>Small branded window shown while the WebView2 engine is installed (only on PCs that lack it).</summary>
    class PrepareForm : Form
    {
        readonly Timer anim = new Timer { Interval = 16 };
        float t;
        readonly Image logo;

        public PrepareForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            Text = "Mica Setup";
            DoubleBuffered = true;
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size((int)(420 * scale), (int)(260 * scale));
            try { logo = Image.FromStream(new MemoryStream(Program.ReadResource("ui/mica-256.png"))); } catch { }
            anim.Tick += (s, e) => { t += 0.016f; Invalidate(); };
            Shown += (s, e) =>
            {
                anim.Start();
                Task.Run(() => { try { Installer.InstallWebView2(); } catch { } }).ContinueWith(_ => BeginInvoke(new Action(Close)));
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = ClientRectangle;
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(r, Color.FromArgb(94, 200, 245), Color.FromArgb(155, 107, 242), 35f)) g.FillRectangle(br, r);
            int s = r.Height / 3;
            float bob = (float)Math.Sin(t * 2.2) * s * 0.05f;
            if (logo != null) g.DrawImage(logo, (r.Width - s) / 2, r.Height * 0.16f + bob, s, s);
            using (var f = new Font("Segoe UI Semibold", 12.5f))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                g.DrawString("Getting Mica ready…", f, Brushes.White, new RectangleF(0, r.Height * 0.62f, r.Width, 40), sf);
            // indeterminate bar
            float bw = r.Width * 0.5f, bx = (r.Width - bw) / 2, by = r.Height * 0.8f;
            using (var track = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(track, bx, by, bw, 4);
            float seg = bw * 0.3f, pos = (float)((Math.Sin(t * 2.4) + 1) / 2) * (bw - seg);
            g.FillRectangle(Brushes.White, bx + pos, by, seg, 4);
        }
    }
}
