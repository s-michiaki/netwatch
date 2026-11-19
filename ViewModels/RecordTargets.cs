using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using NetWatch.Services;

namespace NetWatch.ViewModels;

/// <summary>記録対象の選択リストの 1 項目（プロセス名単位）。</summary>
public sealed class TargetItem : ObservableObject
{
    public required string Name { get; init; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) Changed?.Invoke(); } }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    private bool _isCommunicating;
    /// <summary>今まさに接続を持っている（リストの上に並べる）。</summary>
    public bool IsCommunicating { get => _isCommunicating; set => Set(ref _isCommunicating, value); }

    internal Action? Changed;
}

/// <summary>
/// 記録対象のプロセス選択。プロセス名で選ぶので、同じアプリの別プロセスや
/// 記録中に再起動したアプリも対象になる。選択内容は設定ファイルに保存する。
/// </summary>
public sealed class RecordTargets : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, TargetItem> _byName = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<TargetItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectCommunicatingCommand { get; }

    public RecordTargets(AppSettings settings)
    {
        _settings = settings;
        _recordAll = settings.RecordAllProcesses;
        foreach (var n in settings.RecordTargets) GetOrAdd(n).IsChecked = true;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => string.IsNullOrWhiteSpace(Search) || ((TargetItem)o).Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(TargetItem.IsChecked), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(TargetItem.IsCommunicating), ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(TargetItem.Name), ListSortDirection.Ascending));

        SelectNoneCommand = new RelayCommand(_ => { foreach (var i in Items) i.IsChecked = false; });
        SelectCommunicatingCommand = new RelayCommand(_ =>
        {
            RecordAll = false;
            foreach (var i in Items.Where(i => i.IsCommunicating)) i.IsChecked = true;
        });
    }

    private bool _recordAll;
    public bool RecordAll
    {
        get => _recordAll;
        set { if (Set(ref _recordAll, value)) { Raise(nameof(RecordSelected)); OnChanged(); } }
    }
    public bool RecordSelected { get => !RecordAll; set => RecordAll = !value; }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) ItemsView.Refresh(); } }

    public IReadOnlyList<string> SelectedNames =>
        Items.Where(i => i.IsChecked).Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>ツールバーのボタンに出す要約。</summary>
    public string Summary
    {
        get
        {
            if (RecordAll) return "すべて";
            var s = SelectedNames;
            return s.Count switch
            {
                0 => "未選択",
                1 => s[0],
                _ => $"{s[0]} ほか {s.Count - 1} 件",
            };
        }
    }

    /// <summary>ポップアップを開くたびに、実行中のプロセス一覧で候補を更新する。</summary>
    public void Refresh(IEnumerable<string> communicating)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0) continue;
                running.Add(p.Id == 4 ? "System" : p.ProcessName + ".exe"); // ProcessInfoCache と同じ表記に揃える
            }
            catch { }
            finally { p.Dispose(); }
        }

        var comm = new HashSet<string>(communicating, StringComparer.OrdinalIgnoreCase);
        foreach (var n in running.Concat(comm)) GetOrAdd(n);

        // 選択されていない・もう動いていないプロセスは候補から外す
        foreach (var item in Items.ToList())
        {
            item.IsRunning = running.Contains(item.Name);
            item.IsCommunicating = comm.Contains(item.Name);
            if (!item.IsChecked && !item.IsRunning && !item.IsCommunicating)
            {
                Items.Remove(item);
                _byName.Remove(item.Name);
            }
        }
        ItemsView.Refresh();
    }

    /// <summary>プロセス一覧の右クリックから対象に追加する。</summary>
    public void Add(string name)
    {
        RecordAll = false;
        GetOrAdd(name).IsChecked = true;
    }

    private TargetItem GetOrAdd(string name)
    {
        if (_byName.TryGetValue(name, out var item)) return item;
        item = new TargetItem { Name = name, Changed = OnChanged };
        _byName[name] = item;
        Items.Add(item);
        return item;
    }

    private void OnChanged()
    {
        Raise(nameof(Summary));
        _settings.RecordAllProcesses = RecordAll;
        _settings.RecordTargets = SelectedNames.ToList();
        _settings.Save();
    }
}
