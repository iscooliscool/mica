using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mica
{
    /// <summary>Settings, bookmarks, history and session, saved as small JSON files in %LOCALAPPDATA%\Mica.</summary>
    class Store
    {
        // MICA_DATA lets tests (and portable/USB setups) use a different folder.
        public static readonly string Dir = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MICA_DATA"))
            ? Environment.GetEnvironmentVariable("MICA_DATA")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mica");
        public static readonly string WallDir = Path.Combine(Dir, "wallpapers");
        public static readonly string ProfileDir = Path.Combine(Dir, "Profile");

        static Store instance;
        /// <summary>One store shared by every window (normal and private).</summary>
        public static Store Instance => instance ?? (instance = new Store());

        public Dictionary<string, object> Settings;
        public List<Dictionary<string, object>> Bookmarks;
        public List<Dictionary<string, object>> History;

        readonly HashSet<string> dirty = new HashSet<string>();
        readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer { Interval = 1500 };

        Store()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(WallDir);
            Settings = Defaults();
            var saved = Read("settings.json");
            if (saved != null) foreach (var kv in saved) Settings[kv.Key] = kv.Value;
            Bookmarks = ReadList("bookmarks.json");
            History = ReadList("history.json");
            saveTimer.Tick += (s, e) => Flush();
        }

        public static Dictionary<string, object> Defaults() => new Dictionary<string, object>
        {
            ["theme"] = "clean",
            ["custom"] = null,
            ["accent"] = "#6b7cf6",
            ["tabs"] = "top",
            ["search"] = "google",
            ["sleepMinutes"] = 5,
            ["potato"] = false,
            ["potatoAsked"] = false,
            ["aiEnabled"] = false,
            ["aiProvider"] = "duck",
            ["aiWidth"] = 400,
            ["showMemory"] = true,
            ["pipButton"] = true,
            ["bookmarksBar"] = false,
            ["animations"] = true,
            ["shortcuts"] = new object[]
            {
                Shortcut("YouTube", "https://www.youtube.com"),
                Shortcut("Gmail", "https://mail.google.com"),
                Shortcut("Wikipedia", "https://www.wikipedia.org"),
                Shortcut("Drive", "https://drive.google.com"),
                Shortcut("GitHub", "https://github.com"),
                Shortcut("Reddit", "https://www.reddit.com"),
            },
            ["showWeather"] = true,
            ["weather"] = null,
            ["tempUnit"] = "F",
            ["clock24"] = false,
            ["showClock"] = true,
            ["wallpaper"] = "",
            ["wallpaperDim"] = 0.2,
            ["restoreSession"] = true,
            ["firstRunDone"] = false,
            ["updateCheck"] = true,
            ["lastUpdateCheck"] = 0.0,
            ["skipVersion"] = "",
            ["tracking"] = "balanced",
            ["window"] = null,
        };

        static Dictionary<string, object> Shortcut(string t, string u) => new Dictionary<string, object> { ["title"] = t, ["url"] = u };

        // ---------- typed getters ----------
        public string S(string k) => Settings.Str(k, "");
        public bool B(string k) => Settings.Bool(k);
        public double N(string k, double def = 0) => Settings.Num(k, def);

        public void Set(string k, object v) { Settings[k] = v; MarkDirty("settings"); }

        // ---------- persistence ----------
        public void MarkDirty(string what) { dirty.Add(what); saveTimer.Stop(); saveTimer.Start(); }

        public void Flush()
        {
            saveTimer.Stop();
            foreach (var d in dirty.ToList())
            {
                try
                {
                    switch (d)
                    {
                        case "settings": Write("settings.json", Settings); break;
                        case "bookmarks": Write("bookmarks.json", Bookmarks); break;
                        case "history": Write("history.json", History); break;
                    }
                }
                catch { /* disk full or locked: try again next time */ }
            }
            dirty.Clear();
        }

        public static void Write(string name, object data)
        {
            string path = Path.Combine(Dir, name), tmp = path + ".tmp";
            File.WriteAllText(tmp, J.Ser(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static Dictionary<string, object> Read(string name)
        {
            try
            {
                var p = Path.Combine(Dir, name);
                return File.Exists(p) ? J.Obj(File.ReadAllText(p, Encoding.UTF8)) : null;
            }
            catch { return null; }
        }

        static List<Dictionary<string, object>> ReadList(string name)
        {
            try
            {
                var p = Path.Combine(Dir, name);
                if (!File.Exists(p)) return new List<Dictionary<string, object>>();
                var arr = J.S.DeserializeObject(File.ReadAllText(p, Encoding.UTF8)) as object[];
                return arr?.OfType<Dictionary<string, object>>().ToList() ?? new List<Dictionary<string, object>>();
            }
            catch { return new List<Dictionary<string, object>>(); }
        }

        // ---------- history ----------
        public void AddHistory(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("https://mica.example/") || url.StartsWith("https://mica-ui.example/") || url.StartsWith("chrome-extension:") || url.StartsWith("about:") || url.StartsWith("data:")) return;
            var existing = History.FirstOrDefault(h => h.Str("url") == url);
            int visits = 1;
            if (existing != null) { visits = existing.Int("visits", 1) + 1; History.Remove(existing); if (string.IsNullOrEmpty(title)) title = existing.Str("title"); }
            History.Insert(0, new Dictionary<string, object> { ["url"] = url, ["title"] = title ?? "", ["t"] = J.Now(), ["visits"] = visits });
            if (History.Count > 3000) History.RemoveRange(3000, History.Count - 3000);
            MarkDirty("history");
        }

        public void UpdateHistoryTitle(string url, string title)
        {
            var h = History.FirstOrDefault(x => x.Str("url") == url);
            if (h != null && !string.IsNullOrEmpty(title) && h.Str("title") != title) { h["title"] = title; MarkDirty("history"); }
        }

        // ---------- bookmarks ----------
        public bool IsBookmarked(string url) => !string.IsNullOrEmpty(url) && Bookmarks.Any(b => b.Str("url") == url);

        public bool ToggleBookmark(string url, string title)
        {
            var b = Bookmarks.FirstOrDefault(x => x.Str("url") == url);
            if (b != null) { Bookmarks.Remove(b); MarkDirty("bookmarks"); return false; }
            Bookmarks.Add(new Dictionary<string, object> { ["url"] = url, ["title"] = string.IsNullOrEmpty(title) ? url : title, ["t"] = J.Now() });
            MarkDirty("bookmarks");
            return true;
        }
    }
}
