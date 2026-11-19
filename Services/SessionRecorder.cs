using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NetWatch.Native;

namespace NetWatch.Services;

/// <summary>記録中に見つかった宛先 1 件（PID 単位）。</summary>
public sealed class DestinationRecord
{
    public required int Pid { get; init; }
    public required Proto Protocol { get; init; }
    public required string RemoteIp { get; init; }
    public required int RemotePort { get; init; }
    public string? ProcessName { get; set; }
    public string? ProcessPath { get; set; }
    public string? Host { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public long Sent { get; set; }
    public long Received { get; set; }
    internal readonly HashSet<string> LocalEndpoints = new();
}

/// <summary>保存用：プロセス（実行ファイル）× 宛先 でまとめた 1 行。</summary>
public sealed record DestinationSummary(
    string ProcessName,
    string? ProcessPath,
    string Pids,
    string Protocol,
    string RemoteIp,
    int RemotePort,
    string? Host,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Connections,
    long SentBytes,
    long ReceivedBytes);

/// <summary>
/// 記録開始〜停止までに通信した宛先を集計する。
/// 接続テーブル（UI スレッド）と ETW（バックグラウンドスレッド）の両方から呼ばれるので lock で守る。
/// 画面の表示フィルタとは無関係に、ループバック以外のすべての宛先を記録する。
/// </summary>
public sealed class SessionRecorder
{
    private readonly object _lock = new();
    private readonly Dictionary<(int, Proto, string, int), DestinationRecord> _records = new();
    private readonly List<DestinationRecord> _needsInfo = new();

    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? StoppedAt { get; private set; }
    public bool WithTraffic { get; private set; }

    public int Count { get { lock (_lock) return _records.Count; } }

    private HashSet<string>? _targets;
    private readonly HashSet<int> _rejectedPids = new();

    /// <summary>記録対象のプロセス名（例 "chrome.exe"）。null ならすべて。</summary>
    public IReadOnlyCollection<string>? Targets => _targets;

    /// <param name="targets">記録するプロセス名。null ならすべてのプロセスを記録する。</param>
    public void Start(bool withTraffic, IEnumerable<string>? targets = null)
    {
        lock (_lock)
        {
            _records.Clear();
            _needsInfo.Clear();
            _rejectedPids.Clear();
            _targets = targets is null ? null : new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            StartedAt = DateTime.Now;
            StoppedAt = null;
            WithTraffic = withTraffic;
            IsRecording = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsRecording = false;
            StoppedAt = DateTime.Now;
        }
    }

    /// <summary>接続テーブルで見えた接続（UI スレッド）。</summary>
    public void Observe(Proto proto, int pid, string localIp, int localPort, IPAddress? remote, int remotePort)
    {
        if (!IsRecording || !IsRecordable(pid, remote)) return;
        string ra = IpHelper.Normalize(remote);
        lock (_lock)
        {
            var r = GetOrAdd(pid, proto, ra, remotePort);
            if (r is null) return;
            r.LastSeen = DateTime.Now;
            r.LocalEndpoints.Add($"{localIp}:{localPort}");
        }
    }

    /// <summary>ETW の送受信イベント（バックグラウンドスレッド）。</summary>
    public void AddBytes(Proto proto, int pid, string localIp, int localPort, IPAddress remote, int remotePort, int size, bool send)
    {
        if (!IsRecording || !IsRecordable(pid, remote)) return;
        string ra = IpHelper.Normalize(remote);
        lock (_lock)
        {
            var r = GetOrAdd(pid, proto, ra, remotePort);
            if (r is null) return;
            r.LastSeen = DateTime.Now;
            if (send) r.Sent += size; else r.Received += size;
            if (proto == Proto.TCP) r.LocalEndpoints.Add($"{localIp}:{localPort}");
            else r.LocalEndpoints.Add($":{localPort}");
        }
    }

    private static bool IsRecordable(int pid, IPAddress? remote) =>
        pid != 0 && remote is not null && !IpHelper.IsUnspecified(remote) && !IpHelper.IsLoopback(remote);

    private DestinationRecord? GetOrAdd(int pid, Proto proto, string ra, int rp)
    {
        if (_rejectedPids.Contains(pid)) return null;
        var key = (pid, proto, ra, rp);
        if (_records.TryGetValue(key, out var r)) return r;
        var now = DateTime.Now;
        r = new DestinationRecord { Pid = pid, Protocol = proto, RemoteIp = ra, RemotePort = rp, FirstSeen = now, LastSeen = now };
        _records[key] = r;
        _needsInfo.Add(r);
        return r;
    }

    /// <summary>
    /// 新しく見つかった宛先にプロセス名・ホスト名を埋める（UI スレッドから毎秒呼ぶ）。
    /// プロセスがすぐ終了しても名前が残るよう、見つけた直後に引いておく。
    /// </summary>
    public void FillMissing(Func<int, ProcessInfo> process, Func<string, string> host)
    {
        lock (_lock)
        {
            for (int i = _needsInfo.Count - 1; i >= 0; i--)
            {
                var r = _needsInfo[i];
                if (r.ProcessName is null)
                {
                    var info = process(r.Pid);
                    r.ProcessName = info.Name;
                    r.ProcessPath = info.Path;

                    // 対象外のプロセスなら捨てて、以後その PID は記録しない。
                    // （ETW のスレッドではプロセス名を引けないので、ここでまとめて判定する）
                    if (!IsTarget(info.Name))
                    {
                        _rejectedPids.Add(r.Pid);
                        foreach (var k in _records.Where(kv => kv.Value.Pid == r.Pid).Select(kv => kv.Key).ToList())
                            _records.Remove(k);
                        _needsInfo.RemoveAll(x => x.Pid == r.Pid);
                        i = Math.Min(i, _needsInfo.Count);
                        continue;
                    }
                }
                if (string.IsNullOrEmpty(r.Host)) r.Host = NullIfEmpty(host(r.RemoteIp));
                if (!string.IsNullOrEmpty(r.Host)) _needsInfo.RemoveAt(i);
            }
            // ホスト名が分からないまま残るものが溜まりすぎないように
            if (_needsInfo.Count > 5000) _needsInfo.RemoveRange(0, _needsInfo.Count - 5000);
        }
    }

    /// <summary>保存直前にホスト名をもう一度埋めてから、プロセス×宛先 でまとめる。</summary>
    public List<DestinationSummary> Summarize(Func<string, string> host)
    {
        lock (_lock)
        {
            foreach (var r in _records.Values)
                if (string.IsNullOrEmpty(r.Host)) r.Host = NullIfEmpty(host(r.RemoteIp));

            return _records.Values
                .Where(r => r.ProcessName is not null && IsTarget(r.ProcessName))
                .GroupBy(r => (Exe: r.ProcessPath ?? r.ProcessName ?? r.Pid.ToString(), r.Protocol, r.RemoteIp, r.RemotePort))
                .Select(g => new DestinationSummary(
                    ProcessName: g.First().ProcessName ?? "?",
                    ProcessPath: g.First().ProcessPath,
                    Pids: string.Join(" ", g.Select(x => x.Pid).Distinct().OrderBy(x => x)),
                    Protocol: g.Key.Protocol.ToString(),
                    RemoteIp: g.Key.RemoteIp,
                    RemotePort: g.Key.RemotePort,
                    Host: g.Select(x => x.Host).FirstOrDefault(h => !string.IsNullOrEmpty(h)),
                    FirstSeen: g.Min(x => x.FirstSeen),
                    LastSeen: g.Max(x => x.LastSeen),
                    Connections: g.Sum(x => x.LocalEndpoints.Count),
                    SentBytes: g.Sum(x => x.Sent),
                    ReceivedBytes: g.Sum(x => x.Received)))
                .OrderBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(s => s.SentBytes + s.ReceivedBytes)
                .ThenBy(s => s.RemoteIp)
                .ToList();
        }
    }

    private bool IsTarget(string processName) => _targets is null || _targets.Contains(processName);

    /// <summary>名前が確定し、対象と判定済みの宛先の数。</summary>
    public int ConfirmedCount
    {
        get { lock (_lock) return _records.Values.Count(r => r.ProcessName is not null); }
    }

    // ───────── 保存 ─────────

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Excel でそのまま開けるよう UTF-8 (BOM 付き) の CSV で保存する。</summary>
    public static void SaveCsv(string path, IEnumerable<DestinationSummary> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("プロセス名,実行ファイル,PID,プロトコル,リモートIP,リモートポート,ホスト名,初回,最終,接続数,送信バイト,受信バイト");
        foreach (var r in rows)
        {
            sb.AppendJoin(',',
                Csv(r.ProcessName), Csv(r.ProcessPath), Csv(r.Pids), r.Protocol, Csv(r.RemoteIp),
                r.RemotePort.ToString(CultureInfo.InvariantCulture), Csv(r.Host),
                r.FirstSeen.ToString(TimeFormat), r.LastSeen.ToString(TimeFormat),
                r.Connections.ToString(CultureInfo.InvariantCulture),
                r.SentBytes.ToString(CultureInfo.InvariantCulture),
                r.ReceivedBytes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public void SaveJson(string path, IEnumerable<DestinationSummary> rows)
    {
        var doc = new
        {
            tool = "NetWatch",
            startedAt = StartedAt.ToString("o"),
            stoppedAt = StoppedAt?.ToString("o"),
            trafficMeasured = WithTraffic,
            targets = Targets?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            destinations = rows.Select(r => new
            {
                process = r.ProcessName,
                path = r.ProcessPath,
                pids = r.Pids.Split(' ').Select(int.Parse).ToArray(),
                protocol = r.Protocol,
                remoteIp = r.RemoteIp,
                remotePort = r.RemotePort,
                host = r.Host,
                firstSeen = r.FirstSeen.ToString("o"),
                lastSeen = r.LastSeen.ToString("o"),
                connections = r.Connections,
                sentBytes = r.SentBytes,
                receivedBytes = r.ReceivedBytes,
            }),
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, JsonSerializer.Serialize(doc, opts), new UTF8Encoding(false));
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
