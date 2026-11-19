using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetWatch.Services;

namespace NetWatch.ViewModels;

public enum MapMetric { Bytes, Connections, Addresses }

/// <summary>地図上の国 1 つ。</summary>
public sealed class MapCountry : ObservableObject
{
    public required string Iso { get; init; }
    public required string Name { get; init; }
    public required Geometry Geometry { get; init; }

    private Brush _fill = CountryMapViewModel.NoDataBrush;
    public Brush Fill { get => _fill; set => Set(ref _fill, value); }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private string _tip = "";
    public string Tip { get => _tip; set => Set(ref _tip, value); }
}

/// <summary>右側のランキングの 1 行。</summary>
public sealed class CountryRow : ObservableObject
{
    public required string Iso { get; init; }
    public required string Name { get; init; }

    private int _rank;
    public int Rank { get => _rank; set => Set(ref _rank, value); }

    private double _value;
    public double Value { get => _value; set => Set(ref _value, value); }

    private string _valueText = "";
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }

    private double _share;
    /// <summary>0〜1。バーの長さ。</summary>
    public double Share { get => _share; set { if (Set(ref _share, value)) Raise(nameof(ShareText)); } }
    public string ShareText => $"{Share * 100:0.0}%";

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private string _processes = "";
    public string Processes { get => _processes; set => Set(ref _processes, value); }

    private Brush _swatch = CountryMapViewModel.NoDataBrush;
    public Brush Swatch { get => _swatch; set => Set(ref _swatch, value); }
}

/// <summary>選択した国の宛先 IP 一覧の 1 行。</summary>
public sealed record IpRow(string Ip, string Host, string Processes, long Sent, long Received, int Connections)
{
    public string SentText => Fmt.Bytes(Sent);
    public string ReceivedText => Fmt.Bytes(Received);
    public long Total => Sent + Received;
}

public sealed class CountryMapViewModel : ObservableObject, IDisposable
{
    public static readonly Brush NoDataBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3C, 0x43)));
    private static readonly Color[] Ramp =
    {
        Color.FromRgb(0x22, 0x40, 0x6A), // 少
        Color.FromRgb(0x34, 0x78, 0xE0),
        Color.FromRgb(0x36, 0xB3, 0xB0),
        Color.FromRgb(0xE8, 0xC5, 0x47),
        Color.FromRgb(0xF0, 0x74, 0x3C), // 多
    };
    private static List<(string Iso, string Name, Geometry Geo)>? _mapCache;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, CountryRow> _rowsByIso = new();
    private readonly Dictionary<string, MapCountry> _mapByIso = new();
    private Dictionary<string, List<RemoteSnapshot>> _ipsByIso = new();

    public ObservableCollection<MapCountry> MapCountries { get; } = new();
    public ObservableCollection<CountryRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public ObservableCollection<IpRow> SelectedIps { get; } = new();

    public RelayCommand SelectCountryCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand UpdateDbCommand { get; }
    public RelayCommand OpenDbIpCommand { get; }

    public bool IsElevated => _main.IsElevated;
    public bool IsNotElevated => !_main.IsElevated;

    public CountryMapViewModel(MainViewModel main)
    {
        _main = main;
        _metric = main.IsElevated ? MapMetric.Bytes : MapMetric.Connections;

        foreach (var (iso, name, geo) in LoadMap())
        {
            var c = new MapCountry { Iso = iso, Name = name, Geometry = geo, Tip = name };
            MapCountries.Add(c);
            _mapByIso.TryAdd(iso, c);
        }

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.SortDescriptions.Add(new SortDescription(nameof(CountryRow.Value), ListSortDirection.Descending));
        if (RowsView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            live.LiveSortingProperties.Add(nameof(CountryRow.Value));
            live.IsLiveSorting = true;
        }

        SelectCountryCommand = new RelayCommand(p =>
        {
            if (p is string iso) SelectedRow = _rowsByIso.GetValueOrDefault(iso);
        });
        ResetCommand = new RelayCommand(_ =>
        {
            if (MessageBox.Show("国別の集計をリセットしますか？", "NetWatch", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _main.Stats.Reset();
            Refresh();
        });
        UpdateDbCommand = new RelayCommand(async _ => await UpdateDbAsync(), _ => !IsUpdating);
        OpenDbIpCommand = new RelayCommand(_ => Process.Start(new ProcessStartInfo("https://db-ip.com") { UseShellExecute = true }));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // ───────── 表示オプション ─────────

    private MapMetric _metric;
    public MapMetric Metric
    {
        get => _metric;
        set
        {
            if (!Set(ref _metric, value)) return;
            Raise(nameof(MetricIsBytes)); Raise(nameof(MetricIsConnections)); Raise(nameof(MetricIsAddresses));
            Refresh();
        }
    }
    public bool MetricIsBytes { get => Metric == MapMetric.Bytes; set { if (value) Metric = MapMetric.Bytes; } }
    public bool MetricIsConnections { get => Metric == MapMetric.Connections; set { if (value) Metric = MapMetric.Connections; } }
    public bool MetricIsAddresses { get => Metric == MapMetric.Addresses; set { if (value) Metric = MapMetric.Addresses; } }

    private bool _includeLocal;
    public bool IncludeLocal { get => _includeLocal; set { if (Set(ref _includeLocal, value)) Refresh(); } }

    private CountryRow? _selectedRow;
    public CountryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            foreach (var c in MapCountries) c.IsSelected = value is not null && c.Iso == value.Iso;
            Raise(nameof(SelectedTitle));
            UpdateSelectedIps();
        }
    }
    public string SelectedTitle => SelectedRow is null
        ? "国を選ぶと、その国の宛先 IP が表示されます（地図か右の一覧をクリック）"
        : $"{SelectedRow.Name} の宛先 IP";

    private string _topName = "";
    public string TopName { get => _topName; set => Set(ref _topName, value); }

    private string _topDetail = "";
    public string TopDetail { get => _topDetail; set => Set(ref _topDetail, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private string _maxLabel = "";
    public string MaxLabel { get => _maxLabel; set => Set(ref _maxLabel, value); }

    private bool _isUpdating;
    public bool IsUpdating { get => _isUpdating; set => Set(ref _isUpdating, value); }

    private string _dbStatus = "";
    /// <summary>使用中のデータベースの版。</summary>
    public string DbStatus { get => _dbStatus; set => Set(ref _dbStatus, value); }

    private string _dbMessage = "";
    /// <summary>更新ボタンの結果メッセージ。</summary>
    public string DbMessage { get => _dbMessage; set => Set(ref _dbMessage, value); }

    // ───────── 集計 ─────────

    private sealed class Agg
    {
        public required CountryInfo Country;
        public long Sent, Received;
        public int Connections;
        public readonly List<RemoteSnapshot> Ips = new();
        public readonly HashSet<string> Procs = new(StringComparer.OrdinalIgnoreCase);
    }

    private double ValueOf(Agg a) => Metric switch
    {
        MapMetric.Bytes => a.Sent + a.Received,
        MapMetric.Connections => a.Connections,
        _ => a.Ips.Count,
    };

    private string FormatValue(double v) => Metric switch
    {
        MapMetric.Bytes => v <= 0 ? "0 B" : Fmt.Bytes((long)v),
        MapMetric.Connections => $"{v:N0} 接続",
        _ => $"{v:N0} IP",
    };

    private void Refresh()
    {
        var aggs = new Dictionary<string, Agg>();
        foreach (var snap in _main.Stats.Snapshot())
        {
            var country = _main.Geo.Lookup(snap.Ip);
            if (country == CountryInfo.Local && !IncludeLocal) continue;
            if (!aggs.TryGetValue(country.Iso, out var a))
                aggs[country.Iso] = a = new Agg { Country = country };
            a.Sent += snap.Sent;
            a.Received += snap.Received;
            a.Connections += snap.Connections;
            a.Ips.Add(snap);
            foreach (var p in snap.Processes) a.Procs.Add(p);
        }
        _ipsByIso = aggs.ToDictionary(kv => kv.Key, kv => kv.Value.Ips);

        double total = aggs.Values.Sum(ValueOf);
        double max = aggs.Values.Select(ValueOf).DefaultIfEmpty(0).Max();

        // ランキング（行は使い回して選択とスクロールを保つ）
        foreach (var (iso, a) in aggs)
        {
            if (!_rowsByIso.TryGetValue(iso, out var row))
            {
                row = new CountryRow { Iso = iso, Name = a.Country.Name };
                _rowsByIso[iso] = row;
                Rows.Add(row);
            }
            double v = ValueOf(a);
            row.Value = v;
            row.ValueText = FormatValue(v);
            row.Share = total > 0 ? v / total : 0;
            row.Swatch = BrushFor(v, max);
            row.Detail = $"↑ {Fmt.Bytes(a.Sent).OrDash()}  ↓ {Fmt.Bytes(a.Received).OrDash()}  ·  {a.Connections:N0} 接続  ·  {a.Ips.Count:N0} IP";
            row.Processes = string.Join(", ", a.Procs.Take(4)) + (a.Procs.Count > 4 ? $" ほか {a.Procs.Count - 4}" : "");
        }
        foreach (var iso in _rowsByIso.Keys.Except(aggs.Keys).ToList())
        {
            var row = _rowsByIso[iso];
            if (ReferenceEquals(row, SelectedRow)) SelectedRow = null;
            Rows.Remove(row);
            _rowsByIso.Remove(iso);
        }
        int rank = 1;
        foreach (CountryRow r in RowsView) r.Rank = rank++;

        // 地図の色
        foreach (var c in MapCountries)
        {
            if (aggs.TryGetValue(c.Iso, out var a))
            {
                double v = ValueOf(a);
                c.Fill = BrushFor(v, max);
                c.Tip = $"{a.Country.Name}\n{FormatValue(v)}（{(total > 0 ? v / total * 100 : 0):0.0}%）\n{a.Ips.Count:N0} IP · {string.Join(", ", a.Procs.Take(3))}";
            }
            else
            {
                c.Fill = NoDataBrush;
                c.Tip = c.Name;
            }
        }

        // 1 位
        var top = RowsView.Cast<CountryRow>().FirstOrDefault(r => r.Value > 0 && r.Iso is not ("LAN" or "??"));
        TopName = top?.Name ?? "まだデータがありません";
        TopDetail = top is null ? "" : $"{top.ValueText}・全体の {top.ShareText}";
        MaxLabel = FormatValue(max);
        Summary = $"{aggs.Count} か国・地域 / 宛先 {aggs.Values.Sum(a => a.Ips.Count):N0} IP / {_main.Stats.Since:M/d HH:mm} から集計";
        DbStatus = _main.Geo.IsAvailable
            ? $"GeoIP：DB-IP Country Lite（{_main.Geo.BuildDate:yyyy-MM-dd} 版）"
            : $"GeoIP：{_main.Geo.Error}";

        UpdateSelectedIps();
    }

    private void UpdateSelectedIps()
    {
        SelectedIps.Clear();
        if (SelectedRow is null || !_ipsByIso.TryGetValue(SelectedRow.Iso, out var ips)) return;
        foreach (var s in ips.OrderByDescending(s => Metric == MapMetric.Connections ? s.Connections : s.Sent + s.Received)
                             .ThenByDescending(s => s.Connections))
            SelectedIps.Add(new IpRow(s.Ip, _main.ResolveHost(s.Ip), string.Join(", ", s.Processes), s.Sent, s.Received, s.Connections));
    }

    /// <summary>対数スケールで色を決める（1 位だけが目立ちすぎないように）。</summary>
    private static Brush BrushFor(double v, double max)
    {
        if (v <= 0 || max <= 0) return NoDataBrush;
        double t = Math.Log(1 + v) / Math.Log(1 + max);
        t = Math.Clamp(t, 0, 1);
        double seg = t * (Ramp.Length - 1);
        int i = Math.Min((int)seg, Ramp.Length - 2);
        double f = seg - i;
        Color a = Ramp[i], b = Ramp[i + 1];
        var c = Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * f),
            (byte)(a.G + (b.G - a.G) * f),
            (byte)(a.B + (b.B - a.B) * f));
        return Freeze(new SolidColorBrush(c));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ───────── 地図データ ─────────

    /// <summary>Natural Earth（パブリックドメイン）1:110m の国境を正距円筒図法に変換したもの。</summary>
    internal static List<(string Iso, string Name, Geometry Geo)> LoadMap()
    {
        if (_mapCache is not null) return _mapCache;
        var list = new List<(string, string, Geometry)>();
        using var s = typeof(CountryMapViewModel).Assembly.GetManifestResourceStream("NetWatch.Assets.worldmap.tsv");
        if (s is null) return _mapCache = list;
        using var reader = new StreamReader(s);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3) continue;
            var g = Geometry.Parse(parts[2]);
            g.Freeze();
            list.Add((parts[0], parts[1], g));
        }
        return _mapCache = list;
    }

    private async Task UpdateDbAsync()
    {
        IsUpdating = true;
        DbMessage = "ダウンロード中…";
        try
        {
            DbMessage = await _main.Geo.UpdateAsync();
            Refresh();
        }
        catch (Exception ex)
        {
            DbMessage = ex.Message;
        }
        finally
        {
            IsUpdating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public void Dispose() => _timer.Stop();
}

internal static class StringExt
{
    public static string OrDash(this string s) => string.IsNullOrEmpty(s) ? "0 B" : s;
}
