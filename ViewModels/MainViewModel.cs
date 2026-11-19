using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using NetWatch.Native;
using NetWatch.Services;

namespace NetWatch.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ClosedLinger = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NewHighlight = TimeSpan.FromSeconds(3);

    private readonly ProcessInfoCache _procs = new();
    private readonly ReverseDns _reverse = new();
    private readonly EtwMonitor _etw = new();
    private readonly SessionRecorder _recorder = new();

    /// <summary>国別マップ用：起動からの宛先 IP ごとの累計。</summary>
    public RemoteStats Stats { get; } = new();
    public GeoIp Geo { get; } = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, ConnectionRow> _rowsByKey = new();
    private readonly Dictionary<int, ProcessRow> _procsByPid = new();
    private double _lastTickSeconds;
    private long _lastPrune;

    public ObservableCollection<ConnectionRow> Connections { get; } = new();
    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ICollectionView ConnectionsView { get; }
    public ICollectionView ProcessesView { get; }

    public bool IsElevated { get; } = EtwMonitor.IsElevated();
    public bool IsNotElevated => !IsElevated;
    public string EtwStatus { get; }

    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenLocationCommand { get; }
    public RelayCommand LookupIpCommand { get; }
    public RelayCommand ClearProcessFilterCommand { get; }
    public RelayCommand ToggleRecordCommand { get; }
    public RelayCommand AddRecordTargetCommand { get; }
    public RelayCommand OpenCountryMapCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RecordTargets Targets { get; }
    public RelayCommand SaveRecordCommand { get; }

    public MainViewModel()
    {
        ConnectionsView = CollectionViewSource.GetDefaultView(Connections);
        ConnectionsView.Filter = FilterConnection;
        ConnectionsView.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.ProcessName), ListSortDirection.Ascending));
        if (ConnectionsView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            foreach (var p in new[] { nameof(ConnectionRow.TotalRate), nameof(ConnectionRow.SendRate), nameof(ConnectionRow.RecvRate),
                                      nameof(ConnectionRow.Sent), nameof(ConnectionRow.Received), nameof(ConnectionRow.State), nameof(ConnectionRow.Host) })
                live.LiveSortingProperties.Add(p);
            live.IsLiveSorting = true;
        }

        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.TotalRate), ListSortDirection.Descending));
        ProcessesView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Name), ListSortDirection.Ascending));
        if (ProcessesView is ICollectionViewLiveShaping plive && plive.CanChangeLiveSorting)
        {
            plive.LiveSortingProperties.Add(nameof(ProcessRow.TotalRate));
            plive.LiveSortingProperties.Add(nameof(ProcessRow.Connections));
            plive.IsLiveSorting = true;
        }

        Targets = new RecordTargets(AppSettings.Load());
        _etw.Recorder = _recorder;
        _etw.Stats = Stats;
        _etw.Start();
        EtwStatus = !IsElevated
            ? "通常権限：接続一覧のみ（通信量・ドメイン名は管理者権限が必要）"
            : _etw.LastError ?? "ETW 有効：通信量・ドメイン名を取得中";

        RestartAsAdminCommand = new RelayCommand(_ => RestartAsAdmin());
        TogglePauseCommand = new RelayCommand(_ => IsPaused = !IsPaused);
        CopyCommand = new RelayCommand(p => { if (p is string s && s.Length > 0) Clipboard.SetText(s); });
        OpenLocationCommand = new RelayCommand(p =>
        {
            if (p is string path && System.IO.File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
        });
        LookupIpCommand = new RelayCommand(p =>
        {
            if (p is string ip && ip.Length > 0)
                Process.Start(new ProcessStartInfo($"https://ipinfo.io/{ip}") { UseShellExecute = true });
        });
        ClearProcessFilterCommand = new RelayCommand(_ => SelectedProcess = null);
        ToggleRecordCommand = new RelayCommand(_ => ToggleRecording());
        OpenCountryMapCommand = new RelayCommand(_ => CountryMapWindow.ShowFor(this));
        ExitCommand = new RelayCommand(_ => Application.Current.MainWindow?.Close());
        AddRecordTargetCommand = new RelayCommand(p => { if (p is string name && name.Length > 0) Targets.Add(name); }, _ => !IsRecording);
        SaveRecordCommand = new RelayCommand(_ => SaveRecording(), _ => HasRecording && !IsRecording);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // ───────── 表示オプション ─────────

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) ConnectionsView.Refresh(); } }

    private ProcessRow? _selectedProcess;
    public ProcessRow? SelectedProcess { get => _selectedProcess; set { if (Set(ref _selectedProcess, value)) ConnectionsView.Refresh(); } }

    private bool _showUdp = true;
    public bool ShowUdp { get => _showUdp; set { if (Set(ref _showUdp, value)) Tick(); } }

    private bool _showListen;
    public bool ShowListen { get => _showListen; set { if (Set(ref _showListen, value)) Tick(); } }

    private bool _showTimeWait;
    public bool ShowTimeWait { get => _showTimeWait; set { if (Set(ref _showTimeWait, value)) Tick(); } }

    private bool _hideLoopback = true;
    public bool HideLoopback { get => _hideLoopback; set { if (Set(ref _hideLoopback, value)) Tick(); } }

    private bool _reverseLookup;
    public bool ReverseLookup { get => _reverseLookup; set => Set(ref _reverseLookup, value); }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { if (Set(ref _isPaused, value)) Raise(nameof(PauseText)); }
    }
    public string PauseText => IsPaused ? "▶ 再開" : "⏸ 一時停止";

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _totalRate = "";
    public string TotalRate { get => _totalRate; set => Set(ref _totalRate, value); }

    private bool FilterConnection(object o)
    {
        var r = (ConnectionRow)o;
        if (SelectedProcess is not null && r.Pid != SelectedProcess.Pid) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        var q = Search.Trim();
        return r.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Remote.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Host.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Local.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Pid.ToString() == q;
    }

    // ───────── 更新処理 ─────────

    private void Tick()
    {
        if (IsPaused)
        {
            // 表示は止めても記録は続ける
            if (_recorder.IsRecording)
            {
                _recorder.FillMissing(_procs.Get, ResolveHost);
                UpdateRecordStatus();
            }
            foreach (var e in IpHelper.GetAll())
                ObserveBackground(e);
            Stats.ResolveNames(_procs.Get);
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Max(0.2, now - _lastTickSeconds);
        _lastTickSeconds = now;
        long nowTicks = Environment.TickCount64;

        // 1) OS の接続テーブル
        var seen = new HashSet<string>();
        foreach (var e in IpHelper.GetAll())
        {
            ObserveBackground(e);

            if (!Include(e)) continue;

            string la = IpHelper.Normalize(e.LocalAddress);
            string ra = e.Protocol == Proto.TCP ? IpHelper.Normalize(e.RemoteAddress) : "";
            int rp = e.Protocol == Proto.TCP ? e.RemotePort : 0;
            var tkey = e.Protocol == Proto.TCP
                ? new TrafficKey(Proto.TCP, e.Pid, la, e.LocalPort, ra, rp)
                : new TrafficKey(Proto.UDP, e.Pid, "", e.LocalPort, "", 0);

            var row = GetOrCreateRow(tkey, la, e.LocalPort, ra, rp,
                IpHelper.IsLoopback(e.Protocol == Proto.TCP ? e.RemoteAddress : e.LocalAddress));
            row.State = e.State;
            seen.Add(row.Key);
        }

        // 2) ETW の通信量（テーブル取得の合間に開いて閉じた短命な接続もここで拾う）
        foreach (var (tkey, counter) in _etw.Traffic)
        {
            if (tkey.Protocol == Proto.UDP && !ShowUdp) continue;
            string key = KeyOf(tkey);
            bool known = _rowsByKey.TryGetValue(key, out var row);

            bool recentTraffic = nowTicks - Volatile.Read(ref counter.LastSeenTicks) < 3000;
            if (!known)
            {
                if (!recentTraffic) continue;
                if (HideLoopback && System.Net.IPAddress.TryParse(tkey.RemoteAddress, out var rip) && IpHelper.IsLoopback(rip)) continue;
                row = GetOrCreateRow(tkey, tkey.LocalAddress, tkey.LocalPort, tkey.RemoteAddress, tkey.RemotePort, false);
                row.State = ShortLived;
            }
            if (row!.State == ShortLived && recentTraffic) seen.Add(key);

            long s = Interlocked.Read(ref counter.Sent);
            long r = Interlocked.Read(ref counter.Received);
            long ds = s >= row.LastCounterSent ? s - row.LastCounterSent : s; // カウンタが作り直された場合
            long dr = r >= row.LastCounterRecv ? r - row.LastCounterRecv : r;
            row.LastCounterSent = s;
            row.LastCounterRecv = r;
            row.Sent += ds;
            row.Received += dr;
            row.SendRate = ds / dt;
            row.RecvRate = dr / dt;
            row.LastSeenTick = nowTicks;
            if (tkey.Protocol == Proto.UDP && counter.LastRemote is { } lr) row.Remote = lr;

            if (_procsByPid.TryGetValue(row.Pid, out var pr)) { pr.Sent += ds; pr.Received += dr; }
            else _pendingProcBytes[row.Pid] = (_pendingProcBytes.GetValueOrDefault(row.Pid).s + ds, _pendingProcBytes.GetValueOrDefault(row.Pid).r + dr);
        }

        // 3) 新規・終了・ホスト名の反映
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            var row = Connections[i];
            if (seen.Contains(row.Key))
            {
                if (row.IsClosed) { row.IsClosed = false; row.ClosedAtTicks = 0; }
            }
            else
            {
                if (!row.IsClosed) { row.IsClosed = true; row.ClosedAtTicks = nowTicks; if (row.State != ShortLived) row.State = "閉じた"; }
                if (nowTicks - row.ClosedAtTicks > ClosedLinger.TotalMilliseconds)
                {
                    Connections.RemoveAt(i);
                    _rowsByKey.Remove(row.Key);
                    continue;
                }
            }

            if (row.LastSeenTick != nowTicks) { row.SendRate = 0; row.RecvRate = 0; }
            if (row.IsNew && DateTime.Now - row.FirstSeen > NewHighlight) row.IsNew = false;
            if (string.IsNullOrEmpty(row.Host)) row.Host = ResolveHost(row.RemoteIp);
        }

        UpdateProcesses();
        _firstTickDone = true;
        Stats.ResolveNames(_procs.Get);

        if (_recorder.IsRecording)
        {
            _recorder.FillMissing(_procs.Get, ResolveHost);
            UpdateRecordStatus();
        }

        if (nowTicks - _lastPrune > 30_000)
        {
            _etw.Prune(TimeSpan.FromSeconds(60));
            _lastPrune = nowTicks;
        }
    }

    /// <summary>表示フィルタとは関係なく、記録と国別集計に接続を渡す。</summary>
    private void ObserveBackground(SocketEntry e)
    {
        if (e.Protocol != Proto.TCP || e.State is "LISTEN" or "TIME_WAIT") return;
        string la = IpHelper.Normalize(e.LocalAddress);
        if (_recorder.IsRecording) _recorder.Observe(e.Protocol, e.Pid, la, e.LocalPort, e.RemoteAddress, e.RemotePort);
        Stats.Observe(e.Pid, la, e.LocalPort, e.RemoteAddress, e.RemotePort);
    }

    private const string ShortLived = "(短命)";
    private bool _firstTickDone;
    private readonly Dictionary<int, (long s, long r)> _pendingProcBytes = new();

    private bool Include(SocketEntry e)
    {
        if (e.Protocol == Proto.UDP)
        {
            if (!ShowUdp) return false;
            return !(HideLoopback && IpHelper.IsLoopback(e.LocalAddress));
        }
        if (e.State == "LISTEN") return ShowListen;
        if (e.State == "TIME_WAIT" && !ShowTimeWait) return false;
        if (HideLoopback && IpHelper.IsLoopback(e.RemoteAddress)) return false;
        return true;
    }

    private static string KeyOf(TrafficKey k) =>
        $"{k.Protocol}|{k.Pid}|{k.LocalAddress}|{k.LocalPort}|{k.RemoteAddress}|{k.RemotePort}";

    private ConnectionRow GetOrCreateRow(TrafficKey tkey, string la, int lp, string ra, int rp, bool loopback)
    {
        string key = KeyOf(tkey);
        if (_rowsByKey.TryGetValue(key, out var row)) return row;

        var info = _procs.Get(tkey.Pid);
        row = new ConnectionRow
        {
            Key = key,
            Protocol = tkey.Protocol,
            Pid = tkey.Pid,
            LocalAddress = tkey.Protocol == Proto.UDP && la.Length == 0 ? "*" : la,
            LocalPort = lp,
            RemoteIp = ra,
            RemotePort = rp,
            IsLoopback = loopback,
            ProcessName = info.Name,
            ProcessPath = info.Path,
            Remote = tkey.Protocol == Proto.TCP ? (IpHelper.IsUnspecifiedText(ra) ? "*" : ConnectionRow.Endpoint(ra, rp)) : "",
            IsNew = _firstTickDone,
        };

        // ETW 側で既に数えていた分は「今回の差分」に含めない
        if (_etw.Traffic.TryGetValue(tkey, out var c))
        {
            row.LastCounterSent = Interlocked.Read(ref c.Sent);
            row.LastCounterRecv = Interlocked.Read(ref c.Received);
            row.Sent = row.LastCounterSent;
            row.Received = row.LastCounterRecv;
        }

        _rowsByKey[key] = row;
        Connections.Add(row);
        return row;
    }

    public string ResolveHost(string ip)
    {
        if (string.IsNullOrEmpty(ip) || IpHelper.IsUnspecifiedText(ip)) return "";
        if (_etw.DnsNames.TryGetValue(ip, out var name)) return name;
        if (ReverseLookup) return _reverse.TryGet(ip) ?? "";
        return "";
    }

    private void UpdateProcesses()
    {
        var groups = Connections.Where(c => !c.IsClosed).GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (pid, rows) in groups)
        {
            if (!_procsByPid.TryGetValue(pid, out var p))
            {
                var info = _procs.Get(pid);
                p = new ProcessRow { Pid = pid, Name = info.Name, Path = info.Path };
                if (_pendingProcBytes.Remove(pid, out var pend)) { p.Sent = pend.s; p.Received = pend.r; }
                _procsByPid[pid] = p;
                Processes.Add(p);
            }
            p.Connections = rows.Count;
            p.Remotes = rows.Select(r => r.RemoteIp).Where(ip => ip.Length > 0 && !IpHelper.IsUnspecifiedText(ip)).Distinct().Count();
            double sr = rows.Sum(r => r.SendRate), rr = rows.Sum(r => r.RecvRate);
            p.SendRate = sr;
            p.RecvRate = rr;
        }

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            var p = Processes[i];
            if (groups.ContainsKey(p.Pid)) continue;
            if (ReferenceEquals(p, SelectedProcess)) { p.Connections = 0; p.SendRate = 0; p.RecvRate = 0; continue; }
            Processes.RemoveAt(i);
            _procsByPid.Remove(p.Pid);
        }
        _pendingProcBytes.Clear();

        double send = Processes.Sum(p => p.SendRate), recv = Processes.Sum(p => p.RecvRate);
        TotalRate = IsElevated ? $"↑ {Rate0(send)}   ↓ {Rate0(recv)}" : "";
        Status = $"接続 {Connections.Count(c => !c.IsClosed)} 件 / プロセス {groups.Count} 個 / 最終更新 {DateTime.Now:HH:mm:ss}";
    }

    // ───────── 記録 ─────────

    public bool IsRecording => _recorder.IsRecording;
    public bool IsNotRecording => !_recorder.IsRecording;

    private bool _hasRecording;
    public bool HasRecording { get => _hasRecording; private set => Set(ref _hasRecording, value); }

    public string RecordButtonText => IsRecording ? "■ 停止して保存" : "● 記録開始";

    private string _recordStatus = "";
    public string RecordStatus { get => _recordStatus; private set => Set(ref _recordStatus, value); }

    private void ToggleRecording()
    {
        if (!IsRecording)
        {
            IReadOnlyList<string>? targets = null;
            if (!Targets.RecordAll)
            {
                targets = Targets.SelectedNames;
                if (targets.Count == 0)
                {
                    MessageBox.Show("記録するプロセスが選ばれていません。\n「記録対象」から選ぶか、「すべてのプロセス」にしてください。",
                        "NetWatch", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            _recorder.Start(withTraffic: _etw.TrafficActive, targets);
            HasRecording = false;
            UpdateRecordStatus();
        }
        else
        {
            _recorder.Stop();
            HasRecording = true;
            UpdateRecordStatus();
            SaveRecording();
        }
        Raise(nameof(IsRecording));
        Raise(nameof(IsNotRecording));
        Raise(nameof(RecordButtonText));
    }

    /// <summary>記録対象ポップアップを開いたとき。</summary>
    public void RefreshTargetCandidates() =>
        Targets.Refresh(Processes.Where(p => p.Connections > 0).Select(p => p.Name));

    private void UpdateRecordStatus()
    {
        if (IsRecording)
        {
            var el = DateTime.Now - _recorder.StartedAt;
            RecordStatus = $"記録中 {(int)el.TotalHours:00}:{el.Minutes:00}:{el.Seconds:00}　宛先 {_recorder.ConfirmedCount} 件";
        }
        else if (HasRecording)
        {
            RecordStatus = $"記録済み {_recorder.StartedAt:HH:mm:ss}〜{_recorder.StoppedAt:HH:mm:ss}　宛先 {_recorder.ConfirmedCount} 件";
        }
        else RecordStatus = "";
    }

    private void SaveRecording()
    {
        _recorder.FillMissing(_procs.Get, ResolveHost);
        var rows = _recorder.Summarize(ResolveHost);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "通信先の記録を保存",
            FileName = $"NetWatch_{_recorder.StartedAt:yyyyMMdd_HHmmss}",
            Filter = "CSV (Excel で開けます)|*.csv|JSON|*.json",
            DefaultExt = ".csv",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return; // キャンセルしても「💾 保存」から後で保存できる

        try
        {
            if (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                _recorder.SaveJson(dlg.FileName, rows);
            else
                SessionRecorder.SaveCsv(dlg.FileName, rows);

            RecordStatus = $"保存しました：{System.IO.Path.GetFileName(dlg.FileName)}（宛先 {rows.Count} 件）";
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存に失敗しました。\n" + ex.Message, "NetWatch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>ウィンドウを閉じるときに記録中なら確認する。false を返したら閉じない。</summary>
    public bool ConfirmClose()
    {
        if (!IsRecording) return true;
        var r = MessageBox.Show("記録中です。停止して保存しますか？\n\n「いいえ」で保存せずに終了します。", "NetWatch",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) ToggleRecording();
        return true;
    }

    private static string Rate0(double v) => v < 1 ? "0 B/s" : Fmt.Rate(v);

    private static void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC でキャンセルされた
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _etw.Dispose();
        Geo.Dispose();
    }
}
