using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    /// <summary>
    /// Mica's own download manager. Every download is tracked here (instead of WebView2's
    /// Edge-style download dialog), shown in the toolbar bubble and on mica://downloads.
    /// </summary>
    class DownloadItem
    {
        public int Id;
        public string Name, Path, Url, State = "progress", Reason;   // State: progress, paused, done, canceled, failed
        public long Total, Received, Started, Finished;
        public double Speed;                                       // bytes per second
        public bool Private;
        public object Window;                                      // the MainForm it started in
        public CoreWebView2DownloadOperation Op;
        public CoreWebView2 Source;                                // the page that started it
        public bool Held;                                          // WebView2 is holding it for a safety check (needs the person's OK)
        long lastBytes; DateTime lastTick = DateTime.UtcNow;

        internal void Sample()
        {
            var now = DateTime.UtcNow;
            var dt = (now - lastTick).TotalSeconds;
            if (dt < 0.4) return;
            var inst = (Received - lastBytes) / dt;
            Speed = Speed <= 0 ? inst : Speed * 0.6 + inst * 0.4;
            lastBytes = Received; lastTick = now;
        }

        public object ToJson() => new
        {
            id = Id, name = Name, path = Path, url = Url, state = State, reason = Reason, held = Held && State == "progress",
            total = Total, received = Received, t = Started, done = Finished,
            speed = (long)Speed, canResume = Op != null && State == "paused" && SafeCanResume(),
            exists = State == "done" && !string.IsNullOrEmpty(Path) && File.Exists(Path),
        };

        bool SafeCanResume() { try { return Op.CanResume; } catch { return false; } }
    }

    static class Downloads
    {
        static readonly List<DownloadItem> items = new List<DownloadItem>();
        static int nextId = 1;
        static bool loaded;

        /// <summary>Raised on the UI thread when a download changes (null = the whole list changed).</summary>
        public static event Action<DownloadItem> Changed;

        static string File_ => Path.Combine(Store.Dir, "downloads.json");

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(File_)) return;
                var list = J.S.DeserializeObject(File.ReadAllText(File_)) as object[];
                if (list == null) return;
                foreach (var o in list.OfType<Dictionary<string, object>>())
                    items.Add(new DownloadItem
                    {
                        Id = nextId++, Name = o.Str("name", ""), Path = o.Str("path", ""), Url = o.Str("url", ""),
                        State = o.Str("state", "done"), Reason = o.Str("reason"),
                        Total = (long)o.Num("total"), Received = (long)o.Num("received"),
                        Started = (long)o.Num("t"), Finished = (long)o.Num("done"),
                    });
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                var keep = items.Where(d => !d.Private && d.State != "progress" && d.State != "paused").Take(300)
                    .Select(d => new { name = d.Name, path = d.Path, url = d.Url, state = d.State, reason = d.Reason, total = d.Total, received = d.Received, t = d.Started, done = d.Finished })
                    .ToList();
                Store.Write("downloads.json", keep);
            }
            catch { }
        }

        public static List<DownloadItem> All { get { Load(); return items; } }

        public static DownloadItem Get(int id) => All.FirstOrDefault(d => d.Id == id);

        /// <summary>Starts tracking a download. The caller has already set Handled so the Edge dialog stays hidden.</summary>
        public static DownloadItem Track(CoreWebView2DownloadOperation op, string resultPath, object window, bool isPrivate, CoreWebView2 source = null)
        {
            Load();
            var d = new DownloadItem
            {
                Id = nextId++, Op = op, Window = window, Private = isPrivate, Source = source,
                Path = resultPath, Name = System.IO.Path.GetFileName(resultPath),
                Url = op.Uri, Total = (long)(op.TotalBytesToReceive ?? 0), Started = J.Now(),
            };
            items.Insert(0, d);
            op.BytesReceivedChanged += (s, e) =>
            {
                d.Received = op.BytesReceived;
                d.Held = false;
                if (d.Total <= 0) d.Total = (long)(op.TotalBytesToReceive ?? 0);
                d.Sample();
                Changed?.Invoke(d);
            };
            op.StateChanged += (s, e) =>
            {
                d.Path = op.ResultFilePath ?? d.Path;
                d.Name = System.IO.Path.GetFileName(d.Path);
                switch (op.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        d.State = "done"; d.Received = op.BytesReceived; if (d.Total <= 0) d.Total = d.Received; d.Finished = J.Now(); d.Speed = 0;
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        var r = op.InterruptReason;
                        if (r == CoreWebView2DownloadInterruptReason.UserCanceled) { d.State = "canceled"; d.Reason = null; }
                        else if (r == CoreWebView2DownloadInterruptReason.UserShutdown) { d.State = "canceled"; d.Reason = "Mica was closed"; }
                        else { d.State = "failed"; d.Reason = Friendly(r); }
                        d.Finished = J.Now(); d.Speed = 0;
                        break;
                    case CoreWebView2DownloadState.InProgress:
                        d.State = pausedOps.Contains(op) ? "paused" : "progress";
                        break;
                }
                if (d.State != "progress" && d.State != "paused") { d.Op = null; Save(); }
                Changed?.Invoke(d);
            };
            Changed?.Invoke(d);
            WatchForHold(d);
            return d;
        }

        // WebView2 holds some downloads for a safety check (e.g. a program over plain http, or one SmartScreen
        // doesn't know) and reports nothing until someone approves it in its own prompt. Mica hides that
        // prompt, so after a few silent seconds the download is marked "needs your OK" with a Review button.
        // Also reads the download's real state once a second while it runs: WebView2 doesn't always raise
        // its progress/finished events (seen with fast downloads over plain http), which left Mica showing
        // "Starting… 0 B" for a file that had already finished.
        static async void WatchForHold(DownloadItem d)
        {
            var lastChange = DateTime.UtcNow;
            while (d.Op != null && (d.State == "progress" || d.State == "paused"))
            {
                await System.Threading.Tasks.Task.Delay(1000);
                var op = d.Op;
                if (op == null || d.State == "paused") continue;
                try
                {
                    var st = op.State;
                    var got = op.BytesReceived;
                    if (st == CoreWebView2DownloadState.Completed)
                    {
                        d.Path = op.ResultFilePath ?? d.Path; d.Name = System.IO.Path.GetFileName(d.Path);
                        d.State = "done"; d.Received = got; if (d.Total <= 0) d.Total = got; d.Finished = J.Now(); d.Speed = 0; d.Held = false;
                        d.Op = null; Save(); Changed?.Invoke(d);
                        return;
                    }
                    if (st == CoreWebView2DownloadState.Interrupted) continue;   // StateChanged reports why
                    if (got != d.Received) { d.Received = got; d.Held = false; d.Sample(); lastChange = DateTime.UtcNow; Changed?.Invoke(d); }
                    // Nothing has arrived for a while, or everything has but it isn't finished: Windows Defender
                    // is scanning it, or WebView2 wants the person to OK it (e.g. a program over plain http).
                    else if (!d.Held && (got == 0 || (d.Total > 0 && got >= d.Total)) && (DateTime.UtcNow - lastChange).TotalSeconds >= 4)
                    { d.Held = true; d.Speed = 0; Changed?.Invoke(d); }
                }
                catch { return; }   // the operation is gone
            }
        }

        public static void Review(int id)
        {
            var d = Get(id);
            try { d?.Source?.OpenDefaultDownloadDialog(); } catch { }
        }

        static readonly HashSet<CoreWebView2DownloadOperation> pausedOps = new HashSet<CoreWebView2DownloadOperation>();

        static string Friendly(CoreWebView2DownloadInterruptReason r)
        {
            var n = r.ToString();
            if (n.StartsWith("Network")) return "Network problem";
            if (n == "FileNoSpace") return "Disk is full";
            if (n == "FileNameTooLong") return "File name too long";
            if (n == "FileTooLarge") return "File is too large";
            if (n == "FileAccessDenied") return "Couldn't save here";
            if (n == "FileMalicious" || n == "FileFailedVirusScan") return "Blocked: this file may be harmful";
            if (n == "FileBlockedByPolicy" || n == "FileSecurityCheckFailed") return "Blocked by Windows";
            if (n == "ServerUnauthorized" || n == "ServerForbidden") return "The site didn't allow it";
            if (n.StartsWith("Server")) return "The site had a problem";
            return "Failed";
        }

        // ------------- actions -------------
        public static void Pause(int id)
        {
            var d = Get(id); if (d?.Op == null) return;
            try { d.Op.Pause(); pausedOps.Add(d.Op); d.State = "paused"; d.Speed = 0; Changed?.Invoke(d); } catch { }
        }
        public static void Resume(int id)
        {
            var d = Get(id); if (d?.Op == null) return;
            try { pausedOps.Remove(d.Op); d.Op.Resume(); d.State = "progress"; Changed?.Invoke(d); } catch { }
        }
        public static void Cancel(int id)
        {
            var d = Get(id); if (d?.Op == null) return;
            try { d.Op.Cancel(); } catch { }
        }
        public static void Open(int id)
        {
            var d = Get(id); if (d == null || d.State != "done" || !File.Exists(d.Path)) return;
            try { Process.Start(new ProcessStartInfo(d.Path) { UseShellExecute = true }); } catch { }
        }
        public static void Show(int id)
        {
            var d = Get(id); if (d == null) return;
            try
            {
                if (!string.IsNullOrEmpty(d.Path) && File.Exists(d.Path)) Process.Start("explorer.exe", "/select,\"" + d.Path + "\"");
                else Process.Start("explorer.exe", DefaultFolder());
            }
            catch { }
        }
        public static void Remove(int id)
        {
            var d = Get(id); if (d == null) return;
            if (d.Op != null) try { d.Op.Cancel(); } catch { }
            items.Remove(d); Save(); Changed?.Invoke(null);
        }
        public static void Clear()
        {
            items.RemoveAll(d => d.State != "progress" && d.State != "paused"); Save(); Changed?.Invoke(null);
        }
        public static string DefaultFolder()
        {
            try
            {
                var p = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p;
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
