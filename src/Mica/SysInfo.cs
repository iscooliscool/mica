using System;
using System.IO;
using Microsoft.Win32;

namespace Mica
{
    /// <summary>What kind of PC Mica is running on, used for the tour's recommendations.</summary>
    class SysInfo
    {
        public double RamGB;
        public int Cores;
        public string Cpu = "";
        public bool Slow;
        public int RecommendSleep;
        public bool HasChrome, HasEdge;
        public bool Is64Bit = Environment.Is64BitProcess;

        public static SysInfo Read()
        {
            var s = new SysInfo();
            var m = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(m)) s.RamGB = Math.Round(m.ullTotalPhys / 1073741824.0, 1);
            s.Cores = Environment.ProcessorCount;
            try
            {
                s.Cpu = ((Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string) ?? "").Trim();
            }
            catch { }
            s.Slow = s.RamGB <= 3.2 || s.Cores <= 2;
            s.RecommendSleep = s.RamGB <= 2.5 ? 1 : s.RamGB <= 4.5 ? 3 : s.RamGB <= 8.5 ? 5 : 15;
            s.HasChrome = File.Exists(Importer.BookmarkFile("chrome"));
            s.HasEdge = File.Exists(Importer.BookmarkFile("edge"));
            return s;
        }
    }
}
