using System.Windows;
using NetWatch.ViewModels;

namespace NetWatch;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    internal MainViewModel ViewModel => _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closing += (_, e) => { if (!_vm.ConfirmClose()) e.Cancel = true; };
        Closed += (_, _) => _vm.Dispose(); // ETW セッションを確実に止める
    }

    private void TargetPopup_Opened(object sender, EventArgs e) => _vm.RefreshTargetCandidates();

    private void TargetPopup_Closed(object sender, EventArgs e) => TargetToggle.IsChecked = false;
}
