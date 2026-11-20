using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agmen.Services;

/// <summary>
/// Walks up the parent-process chain. A parent must have started before its child, so a
/// "parent" that started later is a process that reused a dead parent's id, and the walk stops.
/// </summary>
public static class ProcessTree
{
    public sealed record Node(int Pid, string Name);

    /// <summary>The process itself, then its parent, grandparent and so on.</summary>
    public static List<Node> Ancestry(int pid, int maxDepth = 16)
    {
        var chain = new List<Node>();
        int current = pid;
        for (int i = 0; i < maxDepth && current > 4; i++)
        {
            DateTime? started;
            try
            {
                using var p = Process.GetProcessById(current);
                chain.Add(new Node(current, p.ProcessName));
                try { started = p.StartTime; } catch (Exception) { started = null; }
            }
            catch (Exception) { break; }   // gone, or not ours to inspect

            int parent = ParentOf(current);
            if (parent <= 0 || parent == current) break;
            try
            {
                using var candidate = Process.GetProcessById(parent);
                if (started is { } s && candidate.StartTime > s.AddSeconds(1)) break;
            }
            catch (Exception) { break; }
            current = parent;
        }
        return chain;
    }

    /// <summary>"52144 Agmen &lt;- 1234 codex &lt;- 2345 node &lt;- 3456 cmd &lt;- 151660 Agmen", for logs.</summary>
    public static string Describe(int pid) =>
        string.Join(" <- ", Ancestry(pid).Select(n => $"{n.Pid} {n.Name}"));

    /// <summary>The parent's process id, or -1 when the process can't be opened.</summary>
    public static int ParentOf(int pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return -1;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        finally { CloseHandle(handle); }
    }

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass,
        ref PROCESS_BASIC_INFORMATION info, int length, out int returnLength);

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
