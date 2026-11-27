using System;
using System.Collections.Generic;
using System.Linq;

namespace Mica
{
    // Bookmark folders. A bookmark's place is its "folder" path ("Work / Docs"); folders you create
    // yourself are also kept in store.Folders so they exist even while they're empty.
    partial class MainForm
    {
        const string Sep = " / ";

        static string CleanPath(string p) =>
            string.Join(Sep, (p ?? "").Split(new[] { Sep }, StringSplitOptions.None)
                .Select(s => s.Trim()).Where(s => s.Length > 0));

        static string CleanName(string n) => (n ?? "").Replace("/", "-").Trim();

        static bool IsInside(string path, string folder) =>
            path == folder || path.StartsWith(folder + Sep, StringComparison.Ordinal);

        // Folders live on the bookmarks bar unless their top folder came from somewhere else
        // (e.g. Chrome's "Other bookmarks", imported off the bar).
        bool FolderOnBar(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            var top = path.Split(new[] { Sep }, StringSplitOptions.None)[0];
            var made = store.Folders.FirstOrDefault(f => f.Str("path") == top);
            if (made != null) return made.Bool("bar", true);
            var any = store.Bookmarks.FirstOrDefault(b => IsInside(b.Str("folder", ""), top));
            return any == null || any.Bool("bar", true);
        }

        void AddFolder(string parent, string name, bool bar)
        {
            parent = CleanPath(parent);
            name = CleanName(name);
            if (name.Length == 0) return;
            var path = parent.Length == 0 ? name : parent + Sep + name;
            if (!store.Folders.Any(f => f.Str("path") == path))
                store.Folders.Add(new Dictionary<string, object> { ["path"] = path, ["bar"] = parent.Length == 0 ? bar : FolderOnBar(parent), ["t"] = J.Now() });
            store.MarkDirty("bookmarks");
            SendBookmarks();
        }

        void RenameFolder(string path, string name)
        {
            path = CleanPath(path);
            name = CleanName(name);
            if (path.Length == 0 || name.Length == 0) return;
            var cut = path.LastIndexOf(Sep, StringComparison.Ordinal);
            var renamed = (cut < 0 ? "" : path.Substring(0, cut + Sep.Length)) + name;
            if (renamed == path) return;
            string Move(string p) => IsInside(p, path) ? renamed + p.Substring(path.Length) : p;
            foreach (var b in store.Bookmarks) { var f = b.Str("folder", ""); if (IsInside(f, path)) b["folder"] = Move(f); }
            foreach (var f in store.Folders) { var p = f.Str("path", ""); if (IsInside(p, path)) f["path"] = Move(p); }
            store.MarkDirty("bookmarks");
            SendBookmarks();
        }

        // Deletes the folder, everything in it and every folder inside it (the UI asks first).
        void DeleteFolder(string path)
        {
            path = CleanPath(path);
            if (path.Length == 0) return;
            store.Bookmarks.RemoveAll(b => IsInside(b.Str("folder", ""), path));
            store.Folders.RemoveAll(f => IsInside(f.Str("path", ""), path));
            store.MarkDirty("bookmarks");
            SendBookmarks(); SendState();
        }
    }
}
