using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    // Downloads: Mica's own toolbar button + bubble + mica://downloads, never the Edge dialog.
    partial class MainForm
    {
        readonly Timer dlTimer = new Timer { Interval = 250 };
        bool dlDirty;
        readonly HashSet<int> announced = new HashSet<int>();

        void InitDownloads()
        {
            Downloads.Changed += OnDownloadChanged;
            FormClosed += (s, e) => Downloads.Changed -= OnDownloadChanged;
            dlTimer.Tick += (s, e) => { if (dlDirty) { dlDirty = false; SendDownloads(0); } else dlTimer.Stop(); };
        }

        void HookDownloads(CoreWebView2 c)
        {
            c.DownloadStarting += (s, e) =>
            {
                e.Handled = true;   // hides the Edge download dialog; the download itself carries on
                Downloads.Track(e.DownloadOperation, e.ResultFilePath, this, Incognito);
            };
        }

        void OnDownloadChanged(DownloadItem d)
        {
            if (IsDisposed) return;
            if (d != null && d.Window == this && !announced.Contains(d.Id))
            {
                announced.Add(d.Id);
                SendDownloads(d.Id);   // a new download: the UI pops the bubble open
                return;
            }
            // progress ticks are batched; finishing/pausing goes out straight away
            if (d != null && d.State == "progress") { dlDirty = true; if (!dlTimer.Enabled) dlTimer.Start(); return; }
            SendDownloads(0, d != null && d.Window == this && d.State == "done" ? d.Id : 0);
        }

        /// <summary>The toolbar only lists this window's downloads from this session; mica://downloads lists all.</summary>
        void SendDownloads(int started, int finished = 0)
        {
            var mine = Downloads.All.Where(x => x.Window == this).Take(12).Select(x => x.ToJson()).ToList();
            PostUi(new { type = "downloads", list = mine, started, finished });
            var pages = tabs.Where(t => t.Url == "mica://downloads" && t.View != null).ToList();
            if (pages.Count == 0) return;
            var all = AllDownloadsJson();
            foreach (var t in pages) Post(t, new { type = "downloads", list = all });
        }

        List<object> AllDownloadsJson() => Downloads.All.Where(x => !x.Private || Incognito).Take(500).Select(x => x.ToJson()).ToList();

        /// <summary>Download commands from the toolbar bubble and the downloads page.</summary>
        bool HandleDownloadCmd(string cmd, Dictionary<string, object> m, Action<object> reply)
        {
            int id = m.Int("id");
            switch (cmd)
            {
                case "dlList": reply(new { type = "downloads", list = AllDownloadsJson() }); return true;
                case "dlPause": Downloads.Pause(id); return true;
                case "dlResume": Downloads.Resume(id); return true;
                case "dlCancel": Downloads.Cancel(id); return true;
                case "dlOpen": Downloads.Open(id); return true;
                case "dlShow": Downloads.Show(id); return true;
                case "dlRemove": Downloads.Remove(id); return true;
                case "dlClear": Downloads.Clear(); return true;
                case "dlFolder": try { System.Diagnostics.Process.Start("explorer.exe", Downloads.DefaultFolder()); } catch { } return true;
                case "dlRetry":
                    {
                        var d = Downloads.Get(id);
                        if (d != null && !string.IsNullOrEmpty(d.Url) && (d.Url.StartsWith("http://") || d.Url.StartsWith("https://")))
                            active?.View?.CoreWebView2?.Navigate(d.Url);   // the site sends the file again
                        return true;
                    }
            }
            return false;
        }
    }
}
