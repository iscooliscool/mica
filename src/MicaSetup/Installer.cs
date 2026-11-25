using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace MicaSetup
{
    /// <summary>The actual install/uninstall work. Per-user: no admin rights needed.</summary>
    static class Installer
    {
        public const string AppExe = "Mica.exe";
        public const string UninstallExe = "Uninstall Mica.exe";
        public const string Repo = "https://github.com/iscooliscool/mica";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Mica";
        const string WebView2Id = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }
        }

        public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mica");
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mica");
        static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mica.lnk");
        static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mica.lnk");

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var d = k?.GetValue("InstallLocation") as string;
                return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, AppExe)) ? d : null;
            }
        }

        public static string InstalledVersion()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) return InstalledDir() != null ? k?.GetValue("DisplayVersion") as string : null;
        }

        public static bool HadDesktopShortcut() => InstalledDir() == null || File.Exists(DesktopLink);

        // ---------------- WebView2 ----------------
        public static bool HasWebView2()
        {
            foreach (var path in new[] {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2Id,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2Id,
                @"HKEY_CURRENT_USER\Software\Microsoft\EdgeUpdate\Clients\" + WebView2Id })
            {
                var v = Registry.GetValue(path, "pv", null) as string;
                if (!string.IsNullOrEmpty(v) && v != "0.0.0.0") return true;
            }
            return false;
        }

        public static void InstallWebView2()
        {
            var exe = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            File.WriteAllBytes(exe, Program.ReadResource("wv2setup.exe"));
            using (var p = Process.Start(new ProcessStartInfo(exe, "/silent /install") { UseShellExecute = false, CreateNoWindow = true }))
                p.WaitForExit(10 * 60 * 1000);
            try { File.Delete(exe); } catch { }
        }

        // ---------------- install ----------------
        public static void Install(string dir, bool desktopShortcut, Action<int, string> progress)
        {
            progress = progress ?? ((p, s) => { });
            dir = Path.GetFullPath(dir);

            progress(3, "Closing Mica");
            CloseMica(dir);

            progress(6, "Getting things ready");
            Directory.CreateDirectory(dir);
            var oldUi = Path.Combine(dir, "ui");
            if (Directory.Exists(oldUi)) Retry(() => Directory.Delete(oldUi, true));

            using (var zip = new ZipArchive(Program.OpenResource("app.zip"), ZipArchiveMode.Read))
            {
                var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
                long total = Math.Max(1, files.Sum(e => e.Length)), done = 0;
                var root = dir.TrimEnd('\\') + "\\";
                foreach (var e in files)
                {
                    var rel = e.FullName.Replace('/', '\\');
                    var target = Path.GetFullPath(Path.Combine(dir, rel));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the install folder
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Retry(() => e.ExtractToFile(target, true));
                    done += e.Length;
                    progress(8 + (int)(done * 74 / total), "Copying " + e.Name);
                }
            }

            progress(84, "Adding the uninstaller");
            var self = Assembly.GetExecutingAssembly().Location;
            var uninst = Path.Combine(dir, UninstallExe);
            if (!string.Equals(self, uninst, StringComparison.OrdinalIgnoreCase)) Retry(() => File.Copy(self, uninst, true));

            progress(88, "Creating shortcuts");
            var exe = Path.Combine(dir, AppExe);
            Shortcut(StartMenuLink, exe, dir);
            if (desktopShortcut) Shortcut(DesktopLink, exe, dir);
            else if (File.Exists(DesktopLink)) try { File.Delete(DesktopLink); } catch { }

            progress(94, "Registering Mica with Windows");
            Register(dir, exe, uninst);

            progress(100, "Done");
        }

        static void Register(string dir, string exe, string uninst)
        {
            string q(string s) => "\"" + s + "\"";
            var cu = Registry.CurrentUser;
            using (var k = cu.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Mica");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Mica");
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", q(uninst) + " /uninstall");
                k.SetValue("QuietUninstallString", q(uninst) + " /uninstall /silent");
                k.SetValue("URLInfoAbout", Repo);
                k.SetValue("HelpLink", Repo + "/issues");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            // So Mica can be chosen as the default browser in Windows Settings.
            const string b = @"Software\Clients\StartMenuInternet\Mica";
            using (var k = cu.CreateSubKey(b)) k.SetValue("", "Mica");
            using (var k = cu.CreateSubKey(b + @"\DefaultIcon")) k.SetValue("", exe + ",0");
            using (var k = cu.CreateSubKey(b + @"\shell\open\command")) k.SetValue("", q(exe));
            using (var k = cu.CreateSubKey(b + @"\Capabilities"))
            {
                k.SetValue("ApplicationName", "Mica");
                k.SetValue("ApplicationDescription", "A fast, light browser for any PC");
                k.SetValue("ApplicationIcon", exe + ",0");
            }
            using (var k = cu.CreateSubKey(b + @"\Capabilities\URLAssociations")) { k.SetValue("http", "MicaURL"); k.SetValue("https", "MicaURL"); }
            using (var k = cu.CreateSubKey(b + @"\Capabilities\FileAssociations"))
                foreach (var ext in new[] { ".htm", ".html", ".pdf", ".svg" }) k.SetValue(ext, "MicaHTML");
            using (var k = cu.CreateSubKey(b + @"\Capabilities\StartMenu")) k.SetValue("StartMenuInternet", "Mica");
            using (var k = cu.CreateSubKey(@"Software\RegisteredApplications")) k.SetValue("Mica", b + @"\Capabilities");
            foreach (var row in new[] { new[] { "MicaURL", "Mica URL", "1" }, new[] { "MicaHTML", "Mica Document", "" } })
            {
                string cls = row[0], label = row[1];
                bool proto = row[2] == "1";
                using (var k = cu.CreateSubKey(@"Software\Classes\" + cls)) { k.SetValue("", label); if (proto) k.SetValue("URL Protocol", ""); }
                using (var k = cu.CreateSubKey(@"Software\Classes\" + cls + @"\DefaultIcon")) k.SetValue("", exe + ",0");
                using (var k = cu.CreateSubKey(@"Software\Classes\" + cls + @"\shell\open\command")) k.SetValue("", q(exe) + " \"%1\"");
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        // ---------------- uninstall ----------------
        public static void Uninstall(string dir, bool deleteData, Action<int, string> progress)
        {
            progress = progress ?? ((p, s) => { });
            progress(5, "Closing Mica");
            CloseMica(dir);

            progress(20, "Removing shortcuts");
            foreach (var l in new[] { StartMenuLink, DesktopLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }

            progress(35, "Removing Mica from Windows");
            var cu = Registry.CurrentUser;
            foreach (var k in new[] { UninstallKey, @"Software\Clients\StartMenuInternet\Mica", @"Software\Classes\MicaURL", @"Software\Classes\MicaHTML" })
                try { cu.DeleteSubKeyTree(k, false); } catch { }
            try { using (var k = cu.OpenSubKey(@"Software\RegisteredApplications", true)) k?.DeleteValue("Mica", false); } catch { }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            progress(55, "Deleting files");
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && File.Exists(Path.Combine(dir, AppExe)))
                Retry(() => Directory.Delete(dir, true));

            if (deleteData)
            {
                progress(80, "Deleting your Mica data");
                if (Directory.Exists(DataDir)) Retry(() => Directory.Delete(DataDir, true));
            }
            progress(100, "Done");
        }

        // ---------------- helpers ----------------
        public static void Launch(string dir)
        {
            var exe = Path.Combine(dir, AppExe);
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
        }

        static void CloseMica(string dir)
        {
            var exe = Path.GetFullPath(Path.Combine(dir, AppExe));
            foreach (var p in Process.GetProcessesByName("Mica"))
            {
                try
                {
                    if (!string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    p.CloseMainWindow();
                    if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
            Thread.Sleep(300); // let WebView2 child processes release files
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (Exception) when (i < 8) { Thread.Sleep(350); }
            }
        }

        static long DirSize(string d)
        {
            try { return new DirectoryInfo(d).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
        }

        static void Shortcut(string link, string target, string workDir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Mica browser" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        /// <summary>After we exit: deletes the setup's temp files (and the temporary uninstaller copy).</summary>
        public static void CleanUpLater(bool deleteSelf)
        {
            try
            {
                var cmd = "/c ping -n 4 127.0.0.1 >nul";
                if (deleteSelf) cmd += " & del /f /q \"" + Assembly.GetExecutingAssembly().Location + "\"";
                if (SetupForm.TempRoot != null) cmd += " & rd /s /q \"" + SetupForm.TempRoot + "\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
