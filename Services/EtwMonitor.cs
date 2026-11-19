using System.Collections.Concurrent;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using NetWatch.Native;

namespace NetWatch.Services;

/// <summary>ETW で数える通信量のキー。TCP は 4 タプル、UDP はローカルポート単位にまとめる。</summary>
public readonly record struct TrafficKey(Proto Protocol, int Pid, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort);

public sealed class TrafficCounter
{
    public long Sent;
    public long Received;
    public long LastSeenTicks;
    /// <summary>UDP 用：直近の通信相手。</summary>
    public string? LastRemote;
}

/// <summary>
/// ETW（Event Tracing for Windows）を購読して
///  - カーネルの TCP/UDP 送受信イベント → プロセス・接続ごとのバイト数
///  - Microsoft-Windows-DNS-Client → IP アドレスとドメイン名の対応
/// を集計する。管理者権限が必要。
/// </summary>
public sealed class EtwMonitor : IDisposable
{
    private const string KernelSessionName = "NetWatch-Kernel";
    private const string DnsSessionName = "NetWatch-Dns";
    private static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");

    private TraceEventSession? _kernel;
    private TraceEventSession? _dns;
    private readonly List<Thread> _threads = new();

    public ConcurrentDictionary<TrafficKey, TrafficCounter> Traffic { get; } = new();

    /// <summary>IP（正規化済み文字列）→ ドメイン名</summary>
    public ConcurrentDictionary<string, string> DnsNames { get; } = new();

    /// <summary>設定されていれば、送受信イベントを記録にも流す。</summary>
    public SessionRecorder? Recorder { get; set; }

    /// <summary>設定されていれば、宛先 IP ごとの累計（国別マップ用）にも流す。</summary>
    public RemoteStats? Stats { get; set; }

    public bool TrafficActive { get; private set; }
    public bool DnsActive { get; private set; }
    public string? LastError { get; private set; }

    public static bool IsElevated() => TraceEventSession.IsElevated() == true;

    public void Start()
    {
        if (!IsElevated())
        {
            LastError = "管理者権限がないため、通信量と DNS 名は取得できません。";
            return;
        }

        try { StartKernel(); TrafficActive = true; }
        catch (Exception ex) { LastError = "カーネル ETW の開始に失敗: " + ex.Message; }

        try { StartDns(); DnsActive = true; }
        catch (Exception ex) { LastError = (LastError is null ? "" : LastError + " / ") + "DNS ETW の開始に失敗: " + ex.Message; }
    }

    private void StartKernel()
    {
        // Windows 8 以降は任意名のセッションでカーネルプロバイダを有効にできる。
        // 古い環境や失敗時は従来の "NT Kernel Logger" にフォールバックする。
        try
        {
            _kernel = new TraceEventSession(KernelSessionName) { StopOnDispose = true };
            _kernel.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }
        catch
        {
            _kernel?.Dispose();
            _kernel = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true };
            _kernel.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
        }

        var k = _kernel.Source.Kernel;
        k.TcpIpSend += e => Add(Proto.TCP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.TcpIpRecv += e => Add(Proto.TCP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.TcpIpSendIPV6 += e => Add(Proto.TCP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.TcpIpRecvIPV6 += e => Add(Proto.TCP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.UdpIpSend += e => Add(Proto.UDP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.UdpIpRecv += e => Add(Proto.UDP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);
        k.UdpIpSendIPV6 += e => Add(Proto.UDP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, true);
        k.UdpIpRecvIPV6 += e => Add(Proto.UDP, e.ProcessID, e.saddr, e.sport, e.daddr, e.dport, e.size, false);

        RunInBackground(_kernel, "ETW-Kernel");
    }

    private void StartDns()
    {
        _dns = new TraceEventSession(DnsSessionName) { StopOnDispose = true };
        _dns.EnableProvider(DnsClientProvider, TraceEventLevel.Informational, ulong.MaxValue);
        _dns.Source.Dynamic.All += OnDnsEvent;
        RunInBackground(_dns, "ETW-DNS");
    }

    private void RunInBackground(TraceEventSession session, string name)
    {
        var t = new Thread(() =>
        {
            try { session.Source.Process(); }
            catch { /* セッション停止時 */ }
        })
        { IsBackground = true, Name = name };
        t.Start();
        _threads.Add(t);
    }

    // カーネルの TcpIp/UdpIp イベントは送受信どちらでも saddr/sport がローカル側、daddr/dport がリモート側。
    private void Add(Proto proto, int pid, IPAddress local, int lport, IPAddress remote, int rport, int size, bool send)
    {
        if (size <= 0) return;
        string la = IpHelper.Normalize(local);
        string ra = IpHelper.Normalize(remote);

        var key = proto == Proto.TCP
            ? new TrafficKey(proto, pid, la, lport, ra, rport)
            : new TrafficKey(proto, pid, "", lport, "", 0);

        var c = Traffic.GetOrAdd(key, _ => new TrafficCounter());
        if (send) Interlocked.Add(ref c.Sent, size);
        else Interlocked.Add(ref c.Received, size);
        Volatile.Write(ref c.LastSeenTicks, Environment.TickCount64);
        if (proto == Proto.UDP) c.LastRemote = $"{ra}:{rport}";

        Recorder?.AddBytes(proto, pid, la, lport, remote, rport, size, send);
        Stats?.AddBytes(proto, pid, la, lport, remote, rport, size, send);
    }

    // Event 3008: クエリ完了。QueryResults は "type:  5 alias.example.com;::ffff:93.184.216.34;" のような形式。
    private void OnDnsEvent(TraceEvent e)
    {
        if (e.ProviderGuid != DnsClientProvider || (int)e.ID != 3008) return;
        try
        {
            var name = e.PayloadByName("QueryName") as string;
            var results = e.PayloadByName("QueryResults") as string;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(results)) return;

            foreach (var raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (raw.StartsWith("type:", StringComparison.OrdinalIgnoreCase)) continue;
                if (IPAddress.TryParse(raw, out var ip))
                    DnsNames[IpHelper.Normalize(ip)] = name.TrimEnd('.');
            }
        }
        catch { /* 形式が違うイベントは無視 */ }
    }

    /// <summary>しばらく通信のないキーを捨てて、メモリが増え続けないようにする。</summary>
    public void Prune(TimeSpan idle)
    {
        long limit = Environment.TickCount64 - (long)idle.TotalMilliseconds;
        foreach (var kv in Traffic)
            if (Volatile.Read(ref kv.Value.LastSeenTicks) < limit)
                Traffic.TryRemove(kv.Key, out _);
    }

    public void Dispose()
    {
        try { _kernel?.Dispose(); } catch { }
        try { _dns?.Dispose(); } catch { }
    }
}
