using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using NetWatch.Native;
using NetWatch.Services;
using NetWatch.ViewModels;

namespace NetWatch;

/// <summary>
/// `NetWatch.exe --selftest [--out path]` で実行する動作確認。
/// CI（GitHub Actions の Windows ランナー）で、単一ファイル版が実際に動くかを確かめるために使う。
/// 結果はファイルに書き、失敗があれば終了コード 1 で終わる。
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string outPath)
    {
        var log = new StringBuilder();
        int failures = 0;

        void Ok(string name, string detail) => log.AppendLine($"[ OK ] {name}: {detail}");
        void Fail(string name, string detail) { failures++; log.AppendLine($"[FAIL] {name}: {detail}"); }
        void Check(string name, bool ok, string detail) { if (ok) Ok(name, detail); else Fail(name, detail); }

        log.AppendLine($"NetWatch selftest  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"exe={Environment.ProcessPath}");
        log.AppendLine($"baseDir={AppContext.BaseDirectory}");
        log.AppendLine($"os={Environment.OSVersion}  64bit={Environment.Is64BitProcess}");

        bool elevated = false;
        try { elevated = EtwMonitor.IsElevated(); Ok("elevation", elevated ? "管理者" : "通常権限"); }
        catch (Exception ex) { Fail("elevation", ex.ToString()); }

        // 1) 接続テーブル
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var keepAlive = http.GetAsync("https://www.example.com/"); // 接続を 1 本作っておく
            await Task.Delay(1500);
            var entries = IpHelper.GetAll();
            Check("connection table", entries.Count > 0, $"{entries.Count} entries, TCP={entries.Count(e => e.Protocol == Proto.TCP)} UDP={entries.Count(e => e.Protocol == Proto.UDP)}");
            try { await keepAlive; } catch { }
        }
        catch (Exception ex) { Fail("connection table", ex.ToString()); }

        // 2) ETW（通信量と DNS 名）— 単一ファイル版で TraceEvent のネイティブ DLL が読めるかの確認も兼ねる
        if (elevated)
        {
            var etw = new EtwMonitor();
            try
            {
                etw.Start();
                Check("etw start", etw.TrafficActive && etw.DnsActive, $"traffic={etw.TrafficActive} dns={etw.DnsActive} error={etw.LastError ?? "-"}");
                await Task.Delay(2000);

                int myPid = Environment.ProcessId;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    foreach (var url in new[] { "https://www.example.org/", "https://www.wikipedia.org/", "https://github.com/" })
                        try { _ = await http.GetByteArrayAsync(url); } catch (Exception ex) { log.AppendLine($"       (fetch {url} failed: {ex.Message})"); }
                }

                long bytes = 0;
                for (int i = 0; i < 20 && bytes == 0; i++)
                {
                    await Task.Delay(500);
                    bytes = etw.Traffic.Where(kv => kv.Key.Pid == myPid).Sum(kv => kv.Value.Sent + kv.Value.Received);
                }
                Check("etw traffic", bytes > 0, $"own process bytes={bytes}, keys total={etw.Traffic.Count}");

                for (int i = 0; i < 10 && etw.DnsNames.IsEmpty; i++) await Task.Delay(500);
                var names = etw.DnsNames.Values.Distinct().Take(5).ToList();
                Check("etw dns", !etw.DnsNames.IsEmpty, $"{etw.DnsNames.Count} ip→name, e.g. {string.Join(", ", names)}");
            }
            catch (Exception ex) { Fail("etw", ex.ToString()); }
            finally { etw.Dispose(); }
        }
        else log.AppendLine("[SKIP] etw: 管理者権限ではないため");

        // 3) GeoIP
        try
        {
            using var geo = new GeoIp();
            var us = geo.Lookup("8.8.8.8");
            var jp = geo.Lookup("133.242.0.1");
            var lan = geo.Lookup("192.168.0.1");
            Check("geoip", geo.IsAvailable && us.Iso == "US" && jp.Iso == "JP" && lan == CountryInfo.Local,
                $"build={geo.BuildDate:yyyy-MM-dd} 8.8.8.8={us.Iso}/{us.Name} 133.242.0.1={jp.Iso}/{jp.Name} 192.168.0.1={lan.Iso} err={geo.Error ?? "-"}");
        }
        catch (Exception ex) { Fail("geoip", ex.ToString()); }

        // 4) 地図データ
        try
        {
            var map = CountryMapViewModel.LoadMap();
            Check("world map", map.Count > 150, $"{map.Count} countries");
        }
        catch (Exception ex) { Fail("world map", ex.ToString()); }

        // 5) 画面（XAML の読み込みと 1 秒ごとの更新が例外なく回るか）
        try
        {
            var main = new MainWindow { ShowActivated = false };
            Application.Current.MainWindow = main;
            main.Show();
            await Task.Delay(3500);
            Ok("main window", $"connections={main.ViewModel.Connections.Count} processes={main.ViewModel.Processes.Count} status=\"{main.ViewModel.Status}\"");

            CountryMapWindow.ShowFor(main.ViewModel);
            await Task.Delay(3000);
            var mapWin = Application.Current.Windows.OfType<CountryMapWindow>().FirstOrDefault();
            Check("country map window", mapWin is not null, mapWin is null ? "開けなかった" : $"top=\"{((CountryMapViewModel)mapWin.DataContext).TopName}\" {((CountryMapViewModel)mapWin.DataContext).Summary}");
            mapWin?.Close();
            main.Close();
        }
        catch (Exception ex) { Fail("windows", ex.ToString()); }

        log.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }
}
