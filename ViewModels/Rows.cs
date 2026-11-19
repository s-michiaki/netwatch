using NetWatch.Native;

namespace NetWatch.ViewModels;

/// <summary>接続一覧の 1 行。更新はインスタンスを使い回して、選択やスクロール位置を保つ。</summary>
public sealed class ConnectionRow : ObservableObject
{
    public required string Key { get; init; }
    public required Proto Protocol { get; init; }
    public required int Pid { get; init; }
    public required string LocalAddress { get; init; }
    public required int LocalPort { get; init; }
    public required string RemoteIp { get; init; }
    public required int RemotePort { get; init; }
    public bool IsLoopback { get; init; }

    public string ProtocolText => Protocol.ToString();
    public string Local => Endpoint(LocalAddress, LocalPort);

    private string _processName = "";
    public string ProcessName { get => _processName; set => Set(ref _processName, value); }

    private string? _processPath;
    public string? ProcessPath { get => _processPath; set => Set(ref _processPath, value); }

    private string _remote = "";
    /// <summary>表示用のリモート（UDP では直近の通信相手が入る）。</summary>
    public string Remote { get => _remote; set => Set(ref _remote, value); }

    private string _host = "";
    public string Host { get => _host; set => Set(ref _host, value); }

    private string _state = "";
    public string State { get => _state; set => Set(ref _state, value); }

    private long _sent, _recv;
    public long Sent { get => _sent; set { if (Set(ref _sent, value)) Raise(nameof(SentText)); } }
    public long Received { get => _recv; set { if (Set(ref _recv, value)) Raise(nameof(ReceivedText)); } }
    public string SentText => Fmt.Bytes(Sent);
    public string ReceivedText => Fmt.Bytes(Received);

    private double _sendRate, _recvRate;
    public double SendRate { get => _sendRate; set { if (Set(ref _sendRate, value)) { Raise(nameof(SendRateText)); Raise(nameof(TotalRate)); } } }
    public double RecvRate { get => _recvRate; set { if (Set(ref _recvRate, value)) { Raise(nameof(RecvRateText)); Raise(nameof(TotalRate)); } } }
    public string SendRateText => Fmt.Rate(SendRate);
    public string RecvRateText => Fmt.Rate(RecvRate);
    public double TotalRate => SendRate + RecvRate;

    private bool _isClosed;
    /// <summary>テーブルから消えた接続。数秒だけ薄く表示してから消す。</summary>
    public bool IsClosed { get => _isClosed; set => Set(ref _isClosed, value); }

    private bool _isNew;
    /// <summary>出現直後の数秒間 true（ハイライト用）。</summary>
    public bool IsNew { get => _isNew; set => Set(ref _isNew, value); }

    public DateTime FirstSeen { get; init; } = DateTime.Now;
    public string FirstSeenText => FirstSeen.ToString("HH:mm:ss");

    internal long ClosedAtTicks;
    internal long LastCounterSent, LastCounterRecv;
    internal long LastSeenTick;

    public static string Endpoint(string addr, int port) =>
        addr.Contains(':') ? $"[{addr}]:{port}" : $"{addr}:{port}";
}

/// <summary>左側のプロセス別サマリーの 1 行。</summary>
public sealed class ProcessRow : ObservableObject
{
    public required int Pid { get; init; }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private string? _path;
    public string? Path { get => _path; set => Set(ref _path, value); }

    private int _connections;
    public int Connections { get => _connections; set => Set(ref _connections, value); }

    private int _remotes;
    public int Remotes { get => _remotes; set => Set(ref _remotes, value); }

    private double _sendRate, _recvRate;
    public double SendRate { get => _sendRate; set { if (Set(ref _sendRate, value)) { Raise(nameof(SendRateText)); Raise(nameof(TotalRate)); } } }
    public double RecvRate { get => _recvRate; set { if (Set(ref _recvRate, value)) { Raise(nameof(RecvRateText)); Raise(nameof(TotalRate)); } } }
    public string SendRateText => Fmt.Rate(SendRate);
    public string RecvRateText => Fmt.Rate(RecvRate);
    public double TotalRate => SendRate + RecvRate;

    private long _sent, _recv;
    public long Sent { get => _sent; set { if (Set(ref _sent, value)) Raise(nameof(TotalText)); } }
    public long Received { get => _recv; set { if (Set(ref _recv, value)) Raise(nameof(TotalText)); } }
    public string TotalText => Fmt.Bytes(Sent + Received);
}
