using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KosToolbox.Core.Execution;

/// <summary>Processes opened by a process (and by its children), by the parent PID Windows keeps for each.</summary>
public static class ProcessTree
{
    /// <summary>
    /// Waits for the process and the ones it opened to finish: NSIS uninstallers copy themselves to %TEMP% and the original exits
    /// right away. Stops early if <paramref name="done"/> stays true for 3 s in a row (e.g. the entry left the list, even
    /// if the uninstaller opened the browser at the end).
    /// </summary>
    public static async Task WaitAsync(int pid, DateTime started, Func<bool> done, CancellationToken ct = default)
    {
        var tracked = new Dictionary<int, DateTime> { [pid] = started };
        var doneSince = (DateTime?)null;
        var deadline = DateTime.UtcNow.AddHours(1);
        while (DateTime.UtcNow < deadline)
        {
            var alive = false;
            foreach (var (id, parent) in Snapshot())
            {
                if (tracked.TryGetValue(id, out var start)) { alive |= StartTime(id) == start; continue; }
                // Child of a tracked process (opened after it: the parent PID may have been reused).
                if (tracked.TryGetValue(parent, out var parentStart) && StartTime(id) is { } childStart && childStart >= parentStart)
                {
                    tracked[id] = childStart;
                    alive = true;
                }
            }
            if (!alive) return;
            if (done()) { doneSince ??= DateTime.UtcNow; if (DateTime.UtcNow - doneSince > TimeSpan.FromSeconds(3)) return; }
            else doneSince = null;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    static DateTime? StartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    static List<(int Id, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e)) list.Add(((int)e.ProcessId, (int)e.ParentProcessId));
        }
        finally { CloseHandle(snap); }
        return list;
    }

    const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
