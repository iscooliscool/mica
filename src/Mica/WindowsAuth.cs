using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Security.Credentials.UI;

namespace Mica
{
    /// <summary>
    /// Asks the person at the PC to prove they're the Windows user before Mica shows, copies or exports a
    /// saved password: Windows Hello (PIN, face, fingerprint) when it's set up, otherwise the Windows
    /// password through the standard Windows Security prompt.
    /// </summary>
    static class WindowsAuth
    {
        [ComImport, Guid("39E050C3-4E74-441A-8DC0-B81104DF949C"), InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        interface IUserConsentVerifierInterop
        {
            [return: MarshalAs(UnmanagedType.IInspectable)]
            object RequestVerificationForWindowAsync(IntPtr appWindow, [MarshalAs(UnmanagedType.HString)] string message, [In] ref Guid riid);
        }

        // IAsyncOperation<UserConsentVerificationResult>
        static Guid AsyncOpIid = new Guid("fd596ffd-2318-558f-9dbe-d21df43764a5");

        public static async Task<bool> Verify(IntPtr owner, string message)
        {
            bool? hello = null;
            try { hello = await Hello(owner, message); } catch { }   // Hello missing or unusable: use the password
            if (hello.HasValue) return hello.Value;
            return await Task.Run(() => PasswordPrompt(owner, message));
        }

        // true = verified, false = the person cancelled, null = Hello isn't available here
        static async Task<bool?> Hello(IntPtr owner, string message)
        {
            var avail = await UserConsentVerifier.CheckAvailabilityAsync();
            if (avail != UserConsentVerifierAvailability.Available) return null;
            var interop = (IUserConsentVerifierInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(UserConsentVerifier));
            var op = (IAsyncOperation<UserConsentVerificationResult>)interop.RequestVerificationForWindowAsync(owner, message, ref AsyncOpIid);
            var r = await op;
            if (r == UserConsentVerificationResult.Verified) return true;
            if (r == UserConsentVerificationResult.Canceled) return false;
            return null;   // Hello busy / not set up after all
        }

        // ---- Windows Security password prompt (CredUI), checked with LogonUser ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDUI_INFO { public int cbSize; public IntPtr hwndParent; public string pszMessageText; public string pszCaptionText; public IntPtr hbmBanner; }

        [DllImport("credui.dll", CharSet = CharSet.Unicode)]
        static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage, IntPtr inBuf, uint inSize, out IntPtr outBuf, out uint outSize, ref bool save, int flags);
        [DllImport("credui.dll", CharSet = CharSet.Unicode)]
        static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buf, uint size, StringBuilder user, ref int userLen, StringBuilder domain, ref int domainLen, StringBuilder pass, ref int passLen);
        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool LogonUser(string user, string domain, string pass, int type, int provider, out IntPtr token);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const int CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200, CRED_PACK_PROTECTED_CREDENTIALS = 0x1;
        const int LOGON32_LOGON_INTERACTIVE = 2, LOGON32_PROVIDER_DEFAULT = 0;

        static bool PasswordPrompt(IntPtr owner, string message)
        {
            int error = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var info = new CREDUI_INFO { cbSize = Marshal.SizeOf(typeof(CREDUI_INFO)), hwndParent = owner, pszCaptionText = "Mica", pszMessageText = message };
                uint pkg = 0; bool save = false;
                if (CredUIPromptForWindowsCredentials(ref info, error, ref pkg, IntPtr.Zero, 0, out var buf, out var size, ref save, CREDUIWIN_ENUMERATE_CURRENT_USER) != 0)
                    return false;   // cancelled
                var u = new StringBuilder(256); var d = new StringBuilder(256); var p = new StringBuilder(256);
                int ul = 256, dl = 256, pl = 256;
                bool unpacked = CredUnPackAuthenticationBuffer(CRED_PACK_PROTECTED_CREDENTIALS, buf, size, u, ref ul, d, ref dl, p, ref pl)
                             || CredUnPackAuthenticationBuffer(0, buf, size, u, ref ul, d, ref dl, p, ref pl);
                // wipe the buffer Windows gave us before freeing it
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                CoTaskMemFree(buf);
                if (!unpacked) return false;
                var user = u.ToString(); var domain = d.ToString();
                if (domain.Length == 0 && user.Contains("\\")) { var parts = user.Split('\\'); domain = parts[0]; user = parts[1]; }
                bool ok = LogonUser(user, domain.Length == 0 ? "." : domain, p.ToString(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token);
                p.Clear();
                if (ok) { CloseHandle(token); return true; }
                error = Marshal.GetLastWin32Error();   // shows "wrong password" on the next prompt
            }
            return false;
        }
    }
}
