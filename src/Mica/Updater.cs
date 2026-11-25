using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

namespace Mica
{
    /// <summary>Checks GitHub Releases for a newer Mica. Never installs anything without the user saying yes.</summary>
    static class Updater
    {
        public const string Repo = "iscooliscool/mica";
        public const string RepoUrl = "https://github.com/" + Repo;

        public class Info { public string Version, Url, Page; }

        public static Version Current => Norm(Assembly.GetExecutingAssembly().GetName().Version);
        public static string CurrentText => Current.Major + "." + Current.Minor + "." + Current.Build;

        static Version Norm(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Mica-Browser-Updater";
            return wc;
        }

        /// <summary>Returns update info when a newer release with an installer exists, otherwise null.</summary>
        public static async Task<Info> Check()
        {
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                var d = J.Obj(await wc.DownloadStringTaskAsync("https://api.github.com/repos/" + Repo + "/releases/latest"));
                var tag = d.Str("tag_name", "").TrimStart('v', 'V');
                if (!Version.TryParse(tag, out var v) || Norm(v) <= Current) return null;
                string url = null;
                if (d.TryGetValue("assets", out var a) && a is object[] assets)
                    url = assets.OfType<System.Collections.Generic.Dictionary<string, object>>()
                        .Select(x => x.Str("browser_download_url", ""))
                        .FirstOrDefault(u => u.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)
                                          && u.StartsWith(RepoUrl + "/releases/download/", StringComparison.OrdinalIgnoreCase));
                if (url == null) return null;
                return new Info { Version = tag, Url = url, Page = d.Str("html_url", RepoUrl + "/releases") };
            }
        }

        public static async Task<string> Download(Info info, Action<int> progress)
        {
            var path = Path.Combine(Path.GetTempPath(), "Mica-Setup-" + info.Version + ".exe");
            using (var wc = Client())
            {
                wc.DownloadProgressChanged += (s, e) => progress?.Invoke(e.ProgressPercentage);
                await wc.DownloadFileTaskAsync(info.Url, path);
            }
            return path;
        }
    }
}
