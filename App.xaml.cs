using System.IO;
using System.Windows;

namespace NetWatch;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int i = Array.IndexOf(e.Args, "--selftest");
        if (i >= 0)
        {
            int o = Array.IndexOf(e.Args, "--out");
            string outPath = o >= 0 && o + 1 < e.Args.Length ? e.Args[o + 1] : Path.Combine(Environment.CurrentDirectory, "selftest.log");
            int code;
            try { code = await SelfTest.RunAsync(outPath); }
            catch (Exception ex)
            {
                File.WriteAllText(outPath, "[FAIL] selftest crashed: " + ex);
                code = 2;
            }
            Shutdown(code);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
