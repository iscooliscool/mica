using System;
using System.Runtime.InteropServices;

namespace Mica
{
    /// <summary>Win32 calls Mica needs for its custom window frame and memory stats.</summary>
    static class Native
    {
        public const int WM_NCCALCSIZE = 0x0083;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr lppos; }

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

        [StructLayout(LayoutKind.Sequential)]
        struct PMC_EX2
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }
        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, ref PMC_EX2 c, uint cb);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Private working set in bytes (what Task Manager shows as "Memory"), 0 if unknown.</summary>
        public static long PrivateWorkingSet(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED*/ | 0x0010 /*VM_READ*/, false, pid);
            if (h == IntPtr.Zero) h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                var c = new PMC_EX2 { cb = (uint)Marshal.SizeOf(typeof(PMC_EX2)) };
                if (GetProcessMemoryInfo(h, ref c, c.cb))
                {
                    long pws = (long)c.PrivateWorkingSetSize.ToUInt64();
                    return pws > 0 ? pws : (long)c.WorkingSetSize.ToUInt64();
                }
                return 0;
            }
            finally { CloseHandle(h); }
        }
    }
}
