using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Agmen.Services;

/// <summary>Ask Windows Restart Manager which live processes have a file open.</summary>
internal static class WindowsFileOwners
{
    private const int MoreData = 234;

    // Null means Windows could not establish ownership. An empty list is a successful query.
    internal static IReadOnlyList<int>? Get(string path)
    {
        try
        {
            if (RmStartSession(out uint session, 0, new StringBuilder(33)) != 0) return null;
            try
            {
                if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return null;
                uint count = 0;
                ProcessInfo[]? entries = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    int result = RmGetList(session, out uint needed, ref count, entries, out _);
                    if (result == 0)
                    {
                        var live = new List<int>();
                        for (int i = 0; i < count; i++)
                        {
                            var entry = entries![i];
                            try
                            {
                                using var process = Process.GetProcessById(entry.Pid);
                                long started = ((long)entry.StartTime.dwHighDateTime << 32) | (uint)entry.StartTime.dwLowDateTime;
                                if (process.StartTime.ToFileTimeUtc() == started) live.Add(entry.Pid);
                            }
                            catch (ArgumentException) { /* this owner has exited */ }
                            catch (InvalidOperationException) { /* this owner has exited */ }
                        }
                        return live;
                    }
                    if (result != MoreData || needed == 0) return null;
                    count = needed;
                    entries = new ProcessInfo[needed];
                }
                return null;
            }
            finally { RmEndSession(session); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public int Pid;
        public FILETIME StartTime;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, Status, TerminalSessionId;
        public int Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, [Out] StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint appCount,
        IntPtr apps, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? processes, out uint rebootReasons);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
