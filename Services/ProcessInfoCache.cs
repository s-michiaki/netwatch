using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetWatch.Services;

public sealed record ProcessInfo(string Name, string? Path);

/// <summary>PID → プロセス名・実行ファイルパスのキャッシュ。</summary>
public sealed class ProcessInfoCache
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private sealed class Entry
    {
        public required ProcessInfo Info;
        public long FetchedTicks;
    }

    private readonly Dictionary<int, Entry> _cache = new();

    public ProcessInfo Get(int pid)
    {
        long now = Environment.TickCount64;
        // PID は再利用されるので 30 秒ごとに取り直す
        if (_cache.TryGetValue(pid, out var e) && now - e.FetchedTicks < 30_000) return e.Info;

        var info = Fetch(pid);
        _cache[pid] = new Entry { Info = info, FetchedTicks = now };
        return info;
    }

    private static ProcessInfo Fetch(int pid)
    {
        if (pid == 0) return new ProcessInfo("System Idle Process", null);
        if (pid == 4) return new ProcessInfo("System", null);

        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            return new ProcessInfo($"(終了済み {pid})", null);
        }

        string? path = null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        return new ProcessInfo(name, path);
    }
}
