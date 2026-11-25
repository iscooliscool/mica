using System;
using Microsoft.Web.WebView2.WinForms;

namespace Mica
{
    /// <summary>One tab. When asleep, View is null and only the URL/title/icon are kept (uses no memory).</summary>
    class BrowserTab
    {
        static int nextId = 1;
        public readonly int Id = nextId++;
        public string Url = "mica://newtab";
        public string Title = "New tab";
        public string Favicon = "";
        public bool Loading, Audible, Muted;
        public DateTime LastActive = DateTime.Now;
        public DateTime HiddenSince = DateTime.Now;
        public bool Suspended;
        public WebView2 View;
        public string PipScriptId;
        public bool Waking;
        public bool Asleep => View == null;
    }
}
