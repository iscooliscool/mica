using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mica
{
    /// <summary>
    /// Mica's password vault. Saved logins live in passwords.dat, encrypted with Windows DPAPI for the
    /// current Windows user (only this Windows account on this PC can decrypt it), with extra entropy
    /// stored next to it. Logins are matched to the exact origin (scheme + host + port) they were saved on.
    /// </summary>
    static class Passwords
    {
        static string FilePath => Path.Combine(Store.Dir, "passwords.dat");
        static string SaltPath => Path.Combine(Store.Dir, "passwords.key");

        public class Login
        {
            public string Id, Origin, User, Pass;
            public long Created, Used;
        }

        static List<Login> logins;
        static HashSet<string> never;

        static byte[] Entropy()
        {
            // Random per-install entropy: another program running as the same user must also know this
            // file to decrypt the vault (DPAPI alone only needs the user's session).
            try
            {
                if (File.Exists(SaltPath)) { var b = File.ReadAllBytes(SaltPath); if (b.Length >= 32) return b; }
                var salt = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                Directory.CreateDirectory(Store.Dir);
                File.WriteAllBytes(SaltPath, salt);
                return salt;
            }
            catch { return Encoding.UTF8.GetBytes("Mica password vault"); }
        }

        static void Load()
        {
            if (logins != null) return;
            logins = new List<Login>();
            never = new HashSet<string>();
            try
            {
                if (!File.Exists(FilePath)) return;
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy(), DataProtectionScope.CurrentUser);
                var d = J.Obj(Encoding.UTF8.GetString(plain));
                Array.Clear(plain, 0, plain.Length);
                if (d.TryGetValue("logins", out var arr) && arr is object[] list)
                    foreach (var o in list.OfType<Dictionary<string, object>>())
                        logins.Add(new Login
                        {
                            Id = o.Str("id") ?? Guid.NewGuid().ToString("N"),
                            Origin = o.Str("origin", ""),
                            User = o.Str("user", ""),
                            Pass = o.Str("pass", ""),
                            Created = (long)o.Num("created"),
                            Used = (long)o.Num("used"),
                        });
                if (d.TryGetValue("never", out var nv) && nv is object[] nlist)
                    foreach (var s in nlist.OfType<string>()) never.Add(s);
            }
            catch
            {
                // Can't decrypt (copied from another PC/user) — keep the file, start empty in memory.
                try { File.Copy(FilePath, FilePath + ".unreadable", true); } catch { }
            }
        }

        static void Save()
        {
            var data = J.Ser(new
            {
                v = 1,
                logins = logins.Select(l => new { id = l.Id, origin = l.Origin, user = l.User, pass = l.Pass, created = l.Created, used = l.Used }),
                never = never.ToArray(),
            });
            var plain = Encoding.UTF8.GetBytes(data);
            var enc = ProtectedData.Protect(plain, Entropy(), DataProtectionScope.CurrentUser);
            Array.Clear(plain, 0, plain.Length);
            Directory.CreateDirectory(Store.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, enc);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }

        /// <summary>"https://example.com" (with a port only when it isn't the default), or null for non-web URLs.</summary>
        public static string OriginOf(string url)
        {
            try
            {
                var u = new Uri(url);
                if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
                return u.Scheme + "://" + u.Host.ToLowerInvariant() + (u.IsDefaultPort ? "" : ":" + u.Port);
            }
            catch { return null; }
        }

        public static string HostOf(string origin)
        {
            try { return new Uri(origin).Host; } catch { return origin; }
        }

        public static List<Login> For(string origin)
        {
            Load();
            return origin == null ? new List<Login>() : logins.Where(l => l.Origin == origin).OrderByDescending(l => l.Used).ToList();
        }

        public static List<Login> All() { Load(); return logins.OrderBy(l => HostOf(l.Origin)).ThenBy(l => l.User).ToList(); }
        public static Login Get(string id) { Load(); return logins.FirstOrDefault(l => l.Id == id); }
        public static bool IsNever(string origin) { Load(); return origin != null && never.Contains(origin); }
        public static List<string> NeverList() { Load(); return never.OrderBy(s => s).ToList(); }

        /// <summary>"new", "update" (same user, new password) or "same" (already saved).</summary>
        public static string Check(string origin, string user, string pass)
        {
            Load();
            var same = logins.FirstOrDefault(l => l.Origin == origin && l.User == user);
            if (same == null) return "new";
            return same.Pass == pass ? "same" : "update";
        }

        public static void Upsert(string origin, string user, string pass)
        {
            Load();
            var now = J.Now();
            var l = logins.FirstOrDefault(x => x.Origin == origin && x.User == user);
            if (l == null) logins.Add(new Login { Id = Guid.NewGuid().ToString("N"), Origin = origin, User = user ?? "", Pass = pass, Created = now, Used = now });
            else { l.Pass = pass; l.Used = now; }
            Save();
        }

        public static void Edit(string id, string user, string pass)
        {
            var l = Get(id);
            if (l == null) return;
            if (user != null) l.User = user;
            if (!string.IsNullOrEmpty(pass)) l.Pass = pass;
            Save();
        }

        public static void Touch(string id) { var l = Get(id); if (l != null) { l.Used = J.Now(); Save(); } }
        public static void Remove(string id) { Load(); logins.RemoveAll(l => l.Id == id); Save(); }
        public static void AddNever(string origin) { Load(); never.Add(origin); Save(); }
        public static void RemoveNever(string origin) { Load(); never.Remove(origin); Save(); }

        // ---------- CSV (Chrome's format: name,url,username,password,note) ----------
        public static int ImportCsv(string path)
        {
            Load();
            var rows = ParseCsv(File.ReadAllText(path, Encoding.UTF8));
            if (rows.Count == 0) return 0;
            var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            int iu = head.IndexOf("url"), iuser = head.IndexOf("username"), ipass = head.IndexOf("password");
            if (iu < 0 || ipass < 0) throw new InvalidDataException("This isn't a passwords CSV file (it needs url and password columns).");
            int n = 0;
            foreach (var r in rows.Skip(1))
            {
                if (r.Count <= Math.Max(iu, ipass)) continue;
                var origin = OriginOf(r[iu]);
                var pass = r[ipass];
                if (origin == null || string.IsNullOrEmpty(pass)) continue;
                var user = iuser >= 0 && iuser < r.Count ? r[iuser] : "";
                if (Check(origin, user, pass) == "same") continue;
                var now = J.Now();
                var l = logins.FirstOrDefault(x => x.Origin == origin && x.User == user);
                if (l == null) logins.Add(new Login { Id = Guid.NewGuid().ToString("N"), Origin = origin, User = user, Pass = pass, Created = now, Used = 0 });
                else l.Pass = pass;
                n++;
            }
            Save();
            return n;
        }

        public static void ExportCsv(string path)
        {
            Load();
            var sb = new StringBuilder("name,url,username,password,note\r\n");
            foreach (var l in All())
                sb.Append(Csv(HostOf(l.Origin))).Append(',').Append(Csv(l.Origin + "/")).Append(',').Append(Csv(l.User)).Append(',').Append(Csv(l.Pass)).Append(",\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool q = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (q)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else q = false; }
                    else cell.Append(c);
                }
                else if (c == '"') q = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                    row = new List<string>();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
