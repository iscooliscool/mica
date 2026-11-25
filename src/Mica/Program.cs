using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    static class Program
    {
        static string PipeName => "Mica.Browser." + Environment.UserName;

        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            // Only one Mica window: a second launch (e.g. clicking a link) hands its URLs to the first.
            var mutex = new Mutex(true, "Local\\Mica.Browser.Instance", out bool first);
            if (!first && SendToRunning(args)) return;

            try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (WebView2RuntimeNotFoundException)
            {
                if (MessageBox.Show("Mica needs the Microsoft WebView2 Runtime (it's free and small).\n\nOpen the download page now?",
                        "Mica", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            OpenWindow(args.Where(a => !a.StartsWith("--")).ToArray(), false);
            StartPipeServer();
            Application.Run(context);
            GC.KeepAlive(mutex);
        }

        // Mica keeps running while any window (normal or private) is open.
        static readonly ApplicationContext context = new ApplicationContext();
        static readonly System.Collections.Generic.List<MainForm> windows = new System.Collections.Generic.List<MainForm>();

        public static MainForm OpenWindow(string[] urls, bool incognito)
        {
            var f = new MainForm(urls, incognito);
            windows.Add(f);
            f.FormClosed += (s, e) =>
            {
                windows.Remove(f);
                if (windows.Count == 0) context.ExitThread();
            };
            f.Show();
            return f;
        }

        /// <summary>Links opened from other apps go to the normal window (a new one if only private windows are open).</summary>
        static void OpenFromOutside(string[] urls)
        {
            var main = windows.FirstOrDefault(w => !w.Incognito);
            if (main == null) OpenWindow(urls, false);
            else main.OpenFromOtherInstance(urls);
        }

        static bool SendToRunning(string[] args)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(2000);
                    var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args.Where(a => !a.StartsWith("--"))));
                    c.Write(bytes, 0, bytes.Length);
                }
                return true;
            }
            catch { return false; }
        }

        static void StartPipeServer()
        {
            var ui = SynchronizationContext.Current;
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            string text;
                            using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                            var urls = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            ui.Post(_ => OpenFromOutside(urls), null);
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            }) { IsBackground = true, Name = "Mica pipe" };
            t.Start();
        }
    }
}
