using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Mica
{
    /// <summary>Copies bookmarks from Chrome or Edge (they keep them in a plain JSON file).</summary>
    static class Importer
    {
        public static string BookmarkFile(string from)
        {
            var la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return from == "edge"
                ? Path.Combine(la, @"Microsoft\Edge\User Data\Default\Bookmarks")
                : Path.Combine(la, @"Google\Chrome\User Data\Default\Bookmarks");
        }

        /// <summary>Returns how many new bookmarks were added, or -1 if the browser isn't installed.</summary>
        public static int Import(Store store, string from)
        {
            var file = BookmarkFile(from);
            if (!File.Exists(file)) return -1;
            string json;
            // Chrome may hold the file open; share-read it.
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8)) json = r.ReadToEnd();

            var roots = J.Obj(json).Sub("roots");
            if (roots == null) return 0;
            var found = new List<Dictionary<string, object>>();
            foreach (var key in new[] { "bookmark_bar", "other", "synced" })
                Walk(roots.Sub(key), found);

            var have = new HashSet<string>(store.Bookmarks.Select(b => b.Str("url")));
            int added = 0;
            foreach (var b in found)
            {
                var url = b.Str("url");
                if (string.IsNullOrEmpty(url) || have.Contains(url)) continue;
                have.Add(url);
                store.Bookmarks.Add(b);
                added++;
            }
            if (added > 0) store.MarkDirty("bookmarks");
            return added;
        }

        static void Walk(Dictionary<string, object> node, List<Dictionary<string, object>> outList)
        {
            if (node == null) return;
            if (node.Str("type") == "url")
            {
                var url = node.Str("url", "");
                if (url.StartsWith("http://") || url.StartsWith("https://"))
                    outList.Add(new Dictionary<string, object> { ["url"] = url, ["title"] = node.Str("name", url), ["t"] = J.Now() });
                return;
            }
            if (node.TryGetValue("children", out var kids) && kids is object[] arr)
                foreach (var k in arr.OfType<Dictionary<string, object>>()) Walk(k, outList);
        }
    }
}
