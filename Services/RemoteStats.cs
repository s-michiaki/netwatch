using System.Collections.Concurrent;
using System.Net;
using NetWatch.Native;

namespace NetWatch.Services;

/// <summary>宛先 IP ごとの累計。</summary>
public sealed class RemoteStat
{
    public long Sent;
    public long Received;
    internal readonly HashSet<int> Pids = new();
    internal readonly HashSet<string> Connections = new();
}

/// <summary>宛先 IP 1 件のスナップショット。</summary>
public sealed record RemoteSnapshot(string Ip, long Sent, long Received, int Connections, IReadOnlyList<string> Processes);

/// <summary>
/// アプリ起動（またはリセット）からの宛先 IP ごとの通信量・接続数・プロセス。
/// 国別マップの元データ。画面の表示フィルタとは関係なく、ループバック以外をすべて数える。
/// ETW スレッドと UI スレッドの両方から呼ばれる。
/// </summary>
public sealed class RemoteStats
{
    private readonly ConcurrentDictionary<string, RemoteStat> _byIp = new();
    private readonly ConcurrentDictionary<int, string> _pidNames = new();
    private readonly ConcurrentQueue<int> _unnamedPids = new();

    public DateTime Since { get; private set; } = DateTime.Now;

    public void Reset()
    {
        _byIp.Clear();
        Since = DateTime.Now;
    }

    /// <summary>接続テーブルで見えた TCP 接続（UI スレッド）。</summary>
    public void Observe(int pid, string localIp, int localPort, IPAddress? remote, int remotePort)
    {
        var s = Get(pid, remote, out _);
        if (s is null) return;
        lock (s) s.Connections.Add($"T|{localIp}|{localPort}|{remotePort}");
    }

    /// <summary>ETW の送受信イベント（バックグラウンドスレッド）。</summary>
    public void AddBytes(Proto proto, int pid, string localIp, int localPort, IPAddress remote, int remotePort, int size, bool send)
    {
        var s = Get(pid, remote, out _);
        if (s is null) return;
        if (send) Interlocked.Add(ref s.Sent, size); else Interlocked.Add(ref s.Received, size);
        lock (s) s.Connections.Add(proto == Proto.TCP ? $"T|{localIp}|{localPort}|{remotePort}" : $"U|{localPort}|{remotePort}");
    }

    private RemoteStat? Get(int pid, IPAddress? remote, out string ip)
    {
        ip = "";
        if (pid == 0 || remote is null || IpHelper.IsUnspecified(remote) || IpHelper.IsLoopback(remote)) return null;
        ip = IpHelper.Normalize(remote);
        var s = _byIp.GetOrAdd(ip, _ => new RemoteStat());
        bool added;
        lock (s) added = s.Pids.Add(pid);
        if (added && !_pidNames.ContainsKey(pid)) _unnamedPids.Enqueue(pid);
        return s;
    }

    /// <summary>新しく出てきた PID のプロセス名を引いておく（UI スレッドから毎秒）。終了後も名前が残るように。</summary>
    public void ResolveNames(Func<int, ProcessInfo> process)
    {
        while (_unnamedPids.TryDequeue(out var pid))
            if (!_pidNames.ContainsKey(pid)) _pidNames[pid] = process(pid).Name;
    }

    public List<RemoteSnapshot> Snapshot()
    {
        var list = new List<RemoteSnapshot>(_byIp.Count);
        foreach (var (ip, s) in _byIp)
        {
            int conns;
            string[] procs;
            lock (s)
            {
                conns = s.Connections.Count;
                procs = s.Pids.Select(p => _pidNames.TryGetValue(p, out var n) ? n : $"PID {p}")
                              .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToArray();
            }
            list.Add(new RemoteSnapshot(ip, Interlocked.Read(ref s.Sent), Interlocked.Read(ref s.Received), conns, procs));
        }
        return list;
    }
}
