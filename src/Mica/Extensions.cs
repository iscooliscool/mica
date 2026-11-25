using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    /// <summary>Chrome extensions: install from the Chrome Web Store, import from other browsers, enable/disable/remove.</summary>
    static class Extensions
    {
        public static string Dir => Path.Combine(Store.Dir, "extensions");
        static string IndexFile => Path.Combine(Dir, "index.json");

        // engine extension id -> folder we installed it from (to find its popup/options pages)
        static Dictionary<string, object> index;
        static Dictionary<string, object> Index
        {
            get
            {
                if (index != null) return index;
                try { index = File.Exists(IndexFile) ? J.Obj(File.ReadAllText(IndexFile)) : new Dictionary<string, object>(); }
                catch { index = new Dictionary<string, object>(); }
                return index;
            }
        }
        static void SaveIndex() { try { Directory.CreateDirectory(Dir); File.WriteAllText(IndexFile, J.Ser(Index)); } catch { } }

        public static Task LoadAll(CoreWebView2Environment env, Store store) => Task.CompletedTask; // the engine remembers installed extensions itself

        /// <summary>Copies an unpacked extension into Mica's folder and installs it. Returns its id, or null.</summary>
        public static async Task<string> InstallFolder(CoreWebView2Profile profile, string folder, bool copy)
        {
            try
            {
                var manifest = J.Obj(File.ReadAllText(Path.Combine(folder, "manifest.json")));
                if (manifest.Count == 0) return null;
                string target = folder;
                if (copy)
                {
                    var name = Path.GetFileName(Path.GetDirectoryName(folder)) ?? Guid.NewGuid().ToString("N");
                    target = Path.Combine(Dir, Sanitize(name));
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    CopyDir(folder, target);
                }
                var meta = Path.Combine(target, "_metadata");
                if (Directory.Exists(meta)) try { Directory.Delete(meta, true); } catch { }
                var ext = await profile.AddBrowserExtensionAsync(target);
                Index[ext.Id] = target;
                SaveIndex();
                return ext.Id;
            }
            catch { return null; }
        }

        /// <summary>Downloads an extension from the Chrome Web Store and installs it.</summary>
        public static async Task<string> InstallFromStore(CoreWebView2Profile profile, string storeId, string engineVersion)
        {
            if (!Regex.IsMatch(storeId ?? "", "^[a-p]{32}$")) throw new ArgumentException("Not a Chrome Web Store extension id");
            var major = (engineVersion ?? "130").Split('.')[0];
            var url = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + major + ".0.0.0" +
                      "&acceptformat=crx2,crx3&x=id%3D" + storeId + "%26installsource%3Dondemand%26uc";
            byte[] crx;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + major + ".0.0.0 Safari/537.36";
                crx = await wc.DownloadDataTaskAsync(url);
            }
            var zip = CrxToZip(crx);
            var target = Path.Combine(Dir, storeId);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            using (var ms = new MemoryStream(zip))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
                foreach (var e in archive.Entries)
                {
                    if (e.Name.Length == 0) continue;
                    var dest = Path.GetFullPath(Path.Combine(target, e.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                }
            }
            var id = await InstallFolder(profile, target, false);
            if (id == null) throw new Exception("The engine refused this extension (it may need features Mica doesn't support yet).");
            return id;
        }

        /// <summary>A .crx file is a small signed header followed by a normal zip.</summary>
        static byte[] CrxToZip(byte[] crx)
        {
            if (crx.Length > 4 && crx[0] == 'P' && crx[1] == 'K') return crx; // already a zip
            if (crx.Length < 16 || Encoding.ASCII.GetString(crx, 0, 4) != "Cr24") throw new Exception("Download wasn't an extension package");
            int version = BitConverter.ToInt32(crx, 4);
            int start = version == 3
                ? 12 + BitConverter.ToInt32(crx, 8)
                : 16 + BitConverter.ToInt32(crx, 8) + BitConverter.ToInt32(crx, 12);
            return crx.Skip(start).ToArray();
        }

        public static async Task<List<object>> List(CoreWebView2Profile profile)
        {
            var list = new List<object>();
            foreach (var e in await profile.GetBrowserExtensionsAsync())
            {
                string popup = null, options = null, icon = null, side = null, name = e.Name, version = "", description = "";
                bool hasAction = false;
                if (Index.TryGetValue(e.Id, out var f) && f is string folder)
                {
                    try
                    {
                        var m = J.Obj(File.ReadAllText(Path.Combine(folder, "manifest.json")));
                        var action = m.Sub("action") ?? m.Sub("browser_action") ?? m.Sub("page_action");
                        hasAction = action != null;
                        popup = action.Str("default_popup");
                        options = m.Sub("options_ui").Str("page") ?? m.Str("options_page");
                        version = m.Str("version", "");
                        description = Localize(folder, m, m.Str("description", ""));
                        name = Localize(folder, m, name);
                        // best icon: prefer the action's icon, else the largest manifest icon.
                        // Mica's own pages can't load chrome-extension:// URLs, so inline the file as a data URL.
                        var iconPath = PickIcon(action.Sub("default_icon")) ?? PickIcon(m.Sub("icons"));
                        icon = IconDataUrl(folder, iconPath);
                        // side panel: the manifest's path, or a conventionally-named page the extension ships
                        side = m.Sub("side_panel").Str("default_path");
                        if (side == null && (hasAction || HasPerm(m, "sidePanel")))
                            foreach (var cand in new[] { "sidepanel.html", "side_panel.html", "sidebar.html", "panel.html", "sidePanel.html" })
                                if (File.Exists(Path.Combine(folder, cand))) { side = cand; break; }
                    }
                    catch { }
                }
                list.Add(new
                {
                    id = e.Id,
                    name,
                    enabled = e.IsEnabled,
                    version,
                    description,
                    icon,
                    hasAction,
                    popup = popup != null ? "chrome-extension://" + e.Id + "/" + popup.TrimStart('/') : null,
                    options = options != null ? "chrome-extension://" + e.Id + "/" + options.TrimStart('/') : null,
                    sidePanel = side != null ? "chrome-extension://" + e.Id + "/" + side.TrimStart('/') : null,
                });
            }
            return list;
        }

        public static async Task SetEnabled(CoreWebView2Profile profile, string id, bool on)
        {
            var e = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id);
            if (e != null) await e.EnableAsync(on);
        }

        public static async Task Remove(CoreWebView2Profile profile, string id)
        {
            var e = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == id);
            if (e != null) await e.RemoveAsync();
            if (Index.TryGetValue(id, out var f) && f is string folder && folder.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(folder, true); } catch { }
            Index.Remove(id);
            SaveIndex();
        }

        /// <summary>Resolves "__MSG_name__" strings from the extension's _locales folder.</summary>
        static string Localize(string folder, Dictionary<string, object> manifest, string s)
        {
            var m = Regex.Match(s ?? "", "^__MSG_(.+)__$");
            if (!m.Success) return s;
            foreach (var loc in new[] { "en_US", "en", manifest.Str("default_locale", "en") })
            {
                var f = Path.Combine(folder, "_locales", loc, "messages.json");
                if (!File.Exists(f)) continue;
                var msgs = J.Obj(File.ReadAllText(f));
                var hit = msgs.FirstOrDefault(kv => string.Equals(kv.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (hit.Value is Dictionary<string, object> d) return d.Str("message", s);
            }
            return s;
        }

        static string IconDataUrl(string folder, string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            try
            {
                var p = Path.Combine(folder, relPath.TrimStart('/').Replace('/', '\\'));
                if (!File.Exists(p)) return null;
                var ext = Path.GetExtension(p).ToLowerInvariant();
                var mime = ext == ".svg" ? "image/svg+xml" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".gif" ? "image/gif" : "image/png";
                var bytes = File.ReadAllBytes(p);
                if (bytes.Length > 512 * 1024) return null;   // keep the toolbar message small
                return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        static string PickIcon(Dictionary<string, object> icons)
        {
            if (icons == null) return null;
            // the biggest one that isn't absurdly large, so toolbar icons stay crisp
            var best = icons.Where(kv => kv.Value is string).OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 0).LastOrDefault(kv => (int.TryParse(kv.Key, out var n) ? n : 999) <= 64);
            if (best.Value is string s) return s;
            return icons.Values.OfType<string>().FirstOrDefault();
        }

        static bool HasPerm(Dictionary<string, object> m, string perm)
        {
            if (m.TryGetValue("permissions", out var p) && p is object[] arr)
                return arr.OfType<string>().Any(x => x == perm);
            return false;
        }

        static string Sanitize(string s) => Regex.Replace(s, @"[^\w\-]", "_");

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
