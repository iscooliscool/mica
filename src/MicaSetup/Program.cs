using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MicaSetup
{
    /// <summary>
    /// Mica's installer/uninstaller. Everything it needs (the app, WebView2 DLLs, its own UI) is
    /// embedded in this one .exe.
    ///   MicaSetup.exe                 install with the custom UI
    ///   MicaSetup.exe /silent         install with no UI (used by Mica's updater), then reopen Mica
    ///   MicaSetup.exe /uninstall      uninstall with the custom UI
    /// </summary>
    static class Program
    {
        public static bool Silent, Uninstall, FromTemp, NoRelaunch;
        public static string TargetDir;

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            foreach (var a in args)
            {
                var x = a.ToLowerInvariant();
                if (x == "/silent" || x == "/verysilent" || x == "/s") Silent = true;
                else if (x == "/uninstall") Uninstall = true;
                else if (x == "/fromtemp") FromTemp = true;
                else if (x == "/norelaunch") NoRelaunch = true;
                else if (x.StartsWith("/dir=")) TargetDir = a.Substring(5).Trim('"');
            }

            // The uninstaller lives in the install folder; run a temp copy so the folder can be deleted.
            if (Uninstall && !FromTemp)
            {
                var dir = TargetDir ?? Installer.InstalledDir() ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var tmp = Path.Combine(Path.GetTempPath(), "MicaUninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, tmp, true);
                Process.Start(tmp, "/uninstall /fromtemp \"/dir=" + dir + "\"" + (Silent ? " /silent" : ""));
                return 0;
            }
            // Only one setup window at a time.
            var mutex = new System.Threading.Mutex(true, "Local\\Mica.Setup.Running", out bool first);
            if (!first) { FocusExisting(); return 0; }
            try { return Run(); }
            finally { GC.KeepAlive(mutex); }
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        static void FocusExisting()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9);
                SetForegroundWindow(p.MainWindowHandle);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (Silent)
            {
                try
                {
                    if (Uninstall) Installer.Uninstall(TargetDir ?? Installer.InstalledDir() ?? Installer.DefaultDir, false, null);
                    else
                    {
                        if (!Installer.HasWebView2()) Installer.InstallWebView2();
                        var dir = TargetDir ?? Installer.InstalledDir() ?? Installer.DefaultDir;
                        Installer.Install(dir, Installer.HadDesktopShortcut(), null);
                        if (!NoRelaunch) Installer.Launch(dir);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Mica setup failed:\n\n" + ex.Message, "Mica", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { if (FromTemp) Installer.CleanUpLater(true); }
            }

            // The installer's own interface uses WebView2 too; get it first if this PC doesn't have it.
            if (!Installer.HasWebView2())
            {
                using (var prep = new PrepareForm()) prep.ShowDialog();
                if (!Installer.HasWebView2())
                {
                    MessageBox.Show("Mica needs the Microsoft WebView2 engine, and it couldn't be installed.\nCheck your internet connection and try again.", "Mica", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }
            Application.Run(new SetupForm());
            Installer.CleanUpLater(FromTemp);
            return 0;
        }

        // ---------- embedded WebView2 DLLs ----------
        static readonly Dictionary<string, Assembly> loaded = new Dictionary<string, Assembly>();

        static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            string res = name == "Microsoft.Web.WebView2.Core" ? "wv2.core.dll" : name == "Microsoft.Web.WebView2.WinForms" ? "wv2.winforms.dll" : null;
            if (res == null) return null;
            lock (loaded)
            {
                if (loaded.TryGetValue(res, out var a)) return a;
                a = Assembly.Load(ReadResource(res));
                loaded[res] = a;
                return a;
            }
        }

        public static byte[] ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new FileNotFoundException("Missing setup resource: " + name);
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static Stream OpenResource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

        public static IEnumerable<string> ResourceNames(string prefix) =>
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(n => n.StartsWith(prefix));
    }
}
