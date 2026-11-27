using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Mica
{
    // Mica's own print dialog. Edge's print preview (what WebView2 shows by default) is replaced with a
    // dialog in Mica's UI; the preview pages are a PDF of the page rendered by Windows (Windows.Data.Pdf),
    // and printing goes through WebView2's PrintAsync / PrintToPdfAsync with the chosen settings.
    partial class MainForm
    {
        const string SaveAsPdf = "\u0001pdf";   // the "Save as PDF" destination
        BrowserTab printTab;
        int printSeq;
        CancellationTokenSource printPreviewCts;

        void OpenPrint()
        {
            var t = active;
            if (t?.View?.CoreWebView2 == null) return;
            CloseExtPopup();
            printTab = t;
            var printers = new List<object>();
            string def = null;
            try
            {
                def = new PrinterSettings().PrinterName;   // the Windows default printer
                foreach (string p in PrinterSettings.InstalledPrinters) printers.Add(new { id = p, name = p });
            }
            catch { }
            SetOverlay(true);
            PostUi(new { type = "printOpen", title = t.Title, printers, def = string.IsNullOrEmpty(def) ? SaveAsPdf : def, pdf = SaveAsPdf });
        }

        CoreWebView2PrintSettings PrintSettingsFrom(Dictionary<string, object> s, bool forPreview)
        {
            var ps = env.CreatePrintSettings();
            ps.Orientation = s.Str("layout") == "landscape" ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
            // paper, in inches
            switch (s.Str("paper", "letter"))
            {
                case "a4": ps.PageWidth = 8.27; ps.PageHeight = 11.69; break;
                case "legal": ps.PageWidth = 8.5; ps.PageHeight = 14; break;
                case "a3": ps.PageWidth = 11.69; ps.PageHeight = 16.54; break;
                default: ps.PageWidth = 8.5; ps.PageHeight = 11; break;
            }
            double m;
            switch (s.Str("margins", "default"))
            {
                case "none": m = 0; break;
                case "small": m = 0.25; break;
                case "large": m = 0.75; break;
                default: m = 0.4; break;
            }
            ps.MarginTop = ps.MarginBottom = ps.MarginLeft = ps.MarginRight = m;
            ps.ScaleFactor = Math.Max(0.1, Math.Min(2.0, s.Num("scale", 100) / 100.0));
            ps.ShouldPrintBackgrounds = s.Bool("backgrounds");
            ps.ShouldPrintHeaderAndFooter = s.Bool("headers") && m > 0;
            if (ps.ShouldPrintHeaderAndFooter) { ps.HeaderTitle = printTab?.Title ?? ""; ps.FooterUri = printTab?.Url ?? ""; }
            var range = (s.Str("pages", "") ?? "").Trim();
            if (range.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(range, @"^[\d\s,\-]+$")) ps.PageRanges = range.Replace(" ", "");
            if (!forPreview)
            {
                ps.Copies = Math.Max(1, Math.Min(99, s.Int("copies", 1)));
                ps.ColorMode = s.Str("color") == "bw" ? CoreWebView2PrintColorMode.Grayscale : CoreWebView2PrintColorMode.Color;
                ps.Duplex = s.Bool("twoSided") ? CoreWebView2PrintDuplex.TwoSidedLongEdge : CoreWebView2PrintDuplex.Default;
                ps.PagesPerSide = Math.Max(1, s.Int("perSheet", 1));
            }
            return ps;
        }

        async Task PrintPreview(Dictionary<string, object> s)
        {
            var seq = ++printSeq;
            printPreviewCts?.Cancel();
            var cts = printPreviewCts = new CancellationTokenSource();
            var c = printTab?.View?.CoreWebView2;
            if (c == null) return;
            try
            {
                var ps = PrintSettingsFrom(s, true);
                using (var pdf = await c.PrintToPdfStreamAsync(ps))
                {
                    if (seq != printSeq) return;
                    var ms = new MemoryStream();
                    await pdf.CopyToAsync(ms);
                    ms.Position = 0;
                    int width = Math.Max(200, Math.Min(900, s.Int("previewWidth", 520)));
                    await RenderPreview(ms, seq, cts, width);
                }
            }
            catch (Exception ex)
            {
                DebugLog("print preview: " + ex);
                if (seq == printSeq) PostUi(new { type = "printPages", seq, count = 0, error = ex.Message });
            }
        }

        // Kept separate so that if Windows' PDF renderer can't load, the failure is caught above.
        async Task RenderPreview(MemoryStream ms, int seq, CancellationTokenSource cts, int width)
        {
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
            int count = (int)doc.PageCount;
            PostUi(new { type = "printPages", seq, count });
            // send pages one by one so the first shows up right away; cap how many we draw
            for (uint i = 0; i < doc.PageCount && i < 60; i++)
            {
                if (cts.IsCancellationRequested || seq != printSeq) return;
                using (var page = doc.GetPage(i))
                using (var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                {
                    var opts = new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)width };
                    await page.RenderToStreamAsync(outStream, opts);
                    var bytes = new byte[outStream.Size];
                    using (var rs = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(outStream.GetInputStreamAt(0)))
                    {
                        int read = 0;
                        while (read < bytes.Length) { int n = await rs.ReadAsync(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                    }
                    PostUi(new { type = "printPage", seq, index = (int)i, src = "data:image/png;base64," + Convert.ToBase64String(bytes) });
                }
            }
        }

        async Task PrintNow(Dictionary<string, object> s)
        {
            var c = printTab?.View?.CoreWebView2;
            if (c == null) return;
            var dest = s.Str("printer", SaveAsPdf);
            try
            {
                if (dest == SaveAsPdf)
                {
                    string path;
                    using (var dlg = new SaveFileDialog
                    {
                        Filter = "PDF|*.pdf",
                        FileName = SafeFileName(printTab.Title) + ".pdf",
                        InitialDirectory = Downloads.DefaultFolder(),
                    })
                    {
                        if (dlg.ShowDialog(this) != DialogResult.OK) return;
                        path = dlg.FileName;
                    }
                    var ok = await c.PrintToPdfAsync(path, PrintSettingsFrom(s, true));
                    PostUi(new { type = "toast", text = ok ? "Saved as PDF" : "Couldn't save the PDF", icon = ok ? "check" : "info" });
                }
                else
                {
                    var ps = PrintSettingsFrom(s, false);
                    ps.PrinterName = dest;
                    PostUi(new { type = "toast", text = "Printing…", icon = "printer" });
                    var r = await c.PrintAsync(ps);
                    string msg;
                    switch (r)
                    {
                        case CoreWebView2PrintStatus.Succeeded: msg = "Sent to " + dest; break;
                        case CoreWebView2PrintStatus.PrinterUnavailable: msg = "That printer isn't available"; break;
                        default: msg = "Printing didn't work"; break;
                    }
                    PostUi(new { type = "toast", text = msg, icon = r == CoreWebView2PrintStatus.Succeeded ? "check" : "info" });
                }
            }
            catch (Exception ex) { PostUi(new { type = "toast", text = "Printing didn't work: " + ex.Message, icon = "info" }); }
        }

        static string SafeFileName(string s)
        {
            s = string.IsNullOrWhiteSpace(s) ? "Page" : s.Trim();
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, ' ');
            return s.Length > 80 ? s.Substring(0, 80).Trim() : s;
        }
    }
}
