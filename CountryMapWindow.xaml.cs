using System.Windows;
using NetWatch.ViewModels;

namespace NetWatch;

public partial class CountryMapWindow : Window
{
    private static CountryMapWindow? _current;
    private readonly CountryMapViewModel _vm;

    private CountryMapWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new CountryMapViewModel(main);
        DataContext = _vm;
        Closed += (_, _) => { _vm.Dispose(); _current = null; };
    }

    /// <summary>1 つだけ開く。すでに開いていれば前面に出す。</summary>
    public static void ShowFor(MainViewModel main)
    {
        if (_current is not null)
        {
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            _current.Activate();
            return;
        }
        _current = new CountryMapWindow(main) { Owner = Application.Current.MainWindow };
        _current.Show();
    }
}
