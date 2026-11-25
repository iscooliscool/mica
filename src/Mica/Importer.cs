using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    /// <summary>
    /// Brings bookmarks, history and extensions over from Chrome, Edge, Brave, Vivaldi and Opera
    /// (every profile). Cookies and saved passwords are locked by those browsers with Windows
    /// "app-bound encryption", so no other program can read them, by design.
    /// </summary>
    static class Importer
    {
        public class Source
        {
            public string Id, Browser, Profile, Dir;
            public bool HasBookmarks, HasHistory;
            public int Extensions;
        }

        static readonly string LA = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string RA = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        static readonly string[][] Browsers =
        {
            new[] { "chrome", "Chrome", Path.Combine(LA, @"Google\Chrome\User Data") },
            new[] { "edge", "Edge", Path.Combine(LA, @"Microsoft\Edge\User Data") },
            new[] { "brave", "Brave", Path.Combine(LA, @"BraveSoftware\Brave-Browser\User Data") },
            new[] { "vivaldi", "Vivaldi", Path.Combine(LA, @"Vivaldi\User Data") },
            new[] { "opera", "Opera", Path.Combine(RA, @"Opera Software\Opera Stable") },
            new[] { "operagx", "Opera GX", Path.Combine(RA, @"Opera Software\Opera GX Stable") },
        };

        public static bool HasBrowser(string id) => Browsers.Any(b => b[0] == id && Directory.Exists(b[2]));

        /// <summary>Every browser profile on this PC that has something to import.</summary>
        public static List<Source> Sources()
        {
            var list = new List<Source>();
            foreach (var b in Browsers)
            {
                if (!Directory.Exists(b[2])) continue;
                var names = new Dictionary<string, string>();
                try
                {
                    var ls = J.Obj(ReadShared(Path.Combine(b[2], "Local State")));
                    var cache = ls.Sub("profile")?.Sub("info_cache");
                    if (cache != null) foreach (var kv in cache) names[kv.Key] = (kv.Value as Dictionary<string, object>).Str("name", kv.Key);
                }
                catch { }
                IEnumerable<string> dirs = b[0].StartsWith("opera")
                    ? new[] { b[2] }
                    : Directory.GetDirectories(b[2]).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile "); });
                foreach (var d in dirs)
                {
                    var key = Path.GetFileName(d);
                    var s = new Source
                    {
                        Id = b[0] + "|" + key,
                        Browser = b[1],
                        Profile = b[0].StartsWith("opera") ? "" : names.TryGetValue(key, out var nm) ? nm : key,
                        Dir = d,
                        HasBookmarks = File.Exists(Path.Combine(d, "Bookmarks")) || File.Exists(Path.Combine(d, "AccountBookmarks")),
                        HasHistory = File.Exists(Path.Combine(d, "History")),
                        Extensions = ExtensionFolders(d).Count,
                    };
                    if (s.HasBookmarks || s.HasHistory || s.Extensions > 0) list.Add(s);
                }
            }
            return list;
        }

        public static Source Find(string id) => Sources().FirstOrDefault(s => s.Id == id);

        public class Result { public int Bookmarks, History, Extensions, ExtensionsFailed; public string Error; }

        public static async Task<Result> Import(Store store, CoreWebView2Profile profile, string sourceId, bool bookmarks, bool history, bool extensions)
        {
            var r = new Result();
            var src = Find(sourceId);
            if (src == null) { r.Error = "That browser profile wasn't found."; return r; }
            try
            {
                if (bookmarks) r.Bookmarks = await Task.Run(() => ImportBookmarks(store, src.Dir));
                if (history) r.History = await Task.Run(() => ImportHistory(store, src.Dir));
                if (extensions && profile != null)
                    foreach (var folder in ExtensionFolders(src.Dir))
                    {
                        var ok = await Mica.Extensions.InstallFolder(profile, folder, true);
                        if (ok != null) r.Extensions++; else r.ExtensionsFailed++;
                    }
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        // ---------------- bookmarks ----------------
        static int ImportBookmarks(Store store, string dir)
        {
            var found = new List<Dictionary<string, object>>();
            foreach (var f in new[] { "Bookmarks", "AccountBookmarks" })
            {
                var file = Path.Combine(dir, f);
                if (!File.Exists(file)) continue;
                var roots = J.Obj(ReadShared(file)).Sub("roots");
                if (roots == null) continue;
                // Keep Chrome's layout: the bookmarks bar (with its folders) goes on Mica's bar,
                // "Other" and "Mobile" bookmarks go into folders of the same name off the bar.
                WalkChildren(roots.Sub("bookmark_bar"), found, "", true);
                WalkChildren(roots.Sub("other"), found, "Other bookmarks", false);
                WalkChildren(roots.Sub("synced"), found, "Mobile bookmarks", false);
            }
            var have = new HashSet<string>(store.Bookmarks.Select(b => b.Str("url") + "|" + b.Str("folder", "")));
            int added = 0;
            foreach (var b in found)
            {
                var url = b.Str("url");
                if (string.IsNullOrEmpty(url) || !have.Add(url + "|" + b.Str("folder", ""))) continue;
                store.Bookmarks.Add(b);
                added++;
            }
            if (added > 0) store.MarkDirty("bookmarks");
            return added;
        }

        static void WalkChildren(Dictionary<string, object> node, List<Dictionary<string, object>> outList, string folder, bool bar)
        {
            if (node != null && node.TryGetValue("children", out var kids) && kids is object[] arr)
                foreach (var k in arr.OfType<Dictionary<string, object>>()) Walk(k, outList, folder, bar);
        }

        static void Walk(Dictionary<string, object> node, List<Dictionary<string, object>> outList, string folder, bool bar)
        {
            if (node == null) return;
            if (node.Str("type") == "url")
            {
                var url = node.Str("url", "");
                if (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("file:"))
                    outList.Add(new Dictionary<string, object> { ["url"] = url, ["title"] = node.Str("name", url), ["folder"] = folder, ["bar"] = bar, ["t"] = J.Now() });
                return;
            }
            var name = (node.Str("name", "") ?? "").Replace(" / ", " - ").Trim();
            if (name == "") name = "Folder";
            WalkChildren(node, outList, string.IsNullOrEmpty(folder) ? name : folder + " / " + name, bar);
        }

        // ---------------- history ----------------
        static int ImportHistory(Store store, string dir)
        {
            var file = Path.Combine(dir, "History");
            if (!File.Exists(file)) return 0;
            var tmp = Path.Combine(Path.GetTempPath(), "mica-history-" + Guid.NewGuid().ToString("N") + ".db");
            CopyShared(file, tmp);
            try
            {
                var rows = Sqlite.Query(tmp, "SELECT url, title, visit_count, last_visit_time FROM urls WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT 3000", 4);
                var byUrl = store.History.ToDictionary(h => h.Str("url"), h => h);
                int added = 0;
                foreach (var row in rows)
                {
                    var url = row[0];
                    if (!(url.StartsWith("http://") || url.StartsWith("https://"))) continue;
                    long.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chromeTime);
                    long unixMs = chromeTime > 0 ? chromeTime / 1000 - 11644473600000L : J.Now();
                    int.TryParse(row[2], out var visits);
                    if (byUrl.TryGetValue(url, out var existing))
                    {
                        if (existing.Num("t") < unixMs) existing["t"] = unixMs;
                        existing["visits"] = Math.Max(existing.Int("visits", 1), visits);
                        continue;
                    }
                    var h = new Dictionary<string, object> { ["url"] = url, ["title"] = row[1], ["t"] = unixMs, ["visits"] = Math.Max(1, visits) };
                    store.History.Add(h);
                    byUrl[url] = h;
                    added++;
                }
                store.History = store.History.OrderByDescending(h => h.Num("t")).Take(3000).ToList();
                store.MarkDirty("history");
                return added;
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ---------------- extensions ----------------
        /// <summary>Unpacked extension folders (newest version of each) in a browser profile.</summary>
        public static List<string> ExtensionFolders(string profileDir)
        {
            var list = new List<string>();
            var root = Path.Combine(profileDir, "Extensions");
            if (!Directory.Exists(root)) return list;
            foreach (var idDir in Directory.GetDirectories(root))
            {
                if (Path.GetFileName(idDir) == "Temp") continue;
                var ver = Directory.GetDirectories(idDir).OrderByDescending(d => d).FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
                if (ver != null) list.Add(ver);
            }
            return list;
        }

        // ---------------- helpers ----------------
        /// <summary>Reads a file another browser has open.</summary>
        static string ReadShared(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) return r.ReadToEnd();
        }

        static void CopyShared(string from, string to)
        {
            using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write)) src.CopyTo(dst);
        }
    }
}
