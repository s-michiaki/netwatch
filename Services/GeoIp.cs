using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using MaxMind.Db;

namespace NetWatch.Services;

public sealed record CountryInfo(string Iso, string Name)
{
    public static readonly CountryInfo Local = new("LAN", "ローカルネットワーク");
    public static readonly CountryInfo Unknown = new("??", "不明");
}

/// <summary>
/// IP アドレス → 国 の判定。DB-IP の「IP to Country Lite」（CC BY 4.0）を使う。
/// アプリに同梱した版を使い、「更新」で最新版を %AppData%\NetWatch にダウンロードする。
/// </summary>
public sealed class GeoIp : IDisposable
{
    private const string FileName = "dbip-country-lite.mmdb";
    private static readonly string BundledPath = Path.Combine(AppContext.BaseDirectory, "Assets", FileName);
    private static readonly string UserPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetWatch", FileName);

    private Reader? _reader;
    private readonly ConcurrentDictionary<string, CountryInfo> _cache = new();
    private readonly object _lock = new();

    public string? Error { get; private set; }
    public DateTime? BuildDate { get; private set; }
    public bool IsAvailable => _reader is not null;

    public GeoIp() => Open();

    private void Open()
    {
        lock (_lock)
        {
            _reader?.Dispose();
            _reader = null;
            _cache.Clear();
            Error = null;

            // ダウンロードした版が同梱版より新しければそちらを使う
            foreach (var path in new[] { UserPath, BundledPath }.Where(File.Exists)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    _reader = new Reader(path, FileAccessMode.Memory);
                    BuildDate = _reader.Metadata.BuildDate;
                    return;
                }
                catch (Exception ex) { Error = ex.Message; }
            }
            Error ??= "GeoIP データベースが見つかりません。";
        }
    }

    public CountryInfo Lookup(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return CountryInfo.Unknown;
        return _cache.GetOrAdd(ip, Resolve);
    }

    private CountryInfo Resolve(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return CountryInfo.Unknown;
        if (IsPrivate(addr)) return CountryInfo.Local;

        lock (_lock)
        {
            if (_reader is null) return CountryInfo.Unknown;
            try
            {
                var d = _reader.Find<Dictionary<string, object>>(addr);
                if (d is null || !d.TryGetValue("country", out var co) || co is not Dictionary<string, object> country)
                    return CountryInfo.Unknown;

                var iso = country.TryGetValue("iso_code", out var i) ? i as string : null;
                if (string.IsNullOrEmpty(iso)) return CountryInfo.Unknown;

                string name = iso;
                if (country.TryGetValue("names", out var n) && n is Dictionary<string, object> names)
                    name = (names.TryGetValue("ja", out var ja) ? ja as string : null)
                        ?? (names.TryGetValue("en", out var en) ? en as string : null)
                        ?? iso;
                return new CountryInfo(iso, name);
            }
            catch { return CountryInfo.Unknown; }
        }
    }

    /// <summary>プライベート・ループバック・リンクローカル・CGNAT・マルチキャストなど、国に紐づかないアドレス。</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224 || b[0] == 0;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = a.GetAddressBytes();
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC; // fc00::/7 (ULA)
        }
        return false;
    }

    /// <summary>
    /// DB-IP から最新版をダウンロードする。月初はまだ当月版が無いことがあるので前月版も試す。
    /// </summary>
    public async Task<string> UpdateAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetWatch/0.4");

        Exception? last = null;
        foreach (var month in new[] { DateTime.UtcNow, DateTime.UtcNow.AddMonths(-1) })
        {
            var url = $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz";
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                resp.EnsureSuccessStatusCode();

                Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
                var tmp = UserPath + ".tmp";
                await using (var gz = new GZipStream(await resp.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress))
                await using (var fs = File.Create(tmp))
                    await gz.CopyToAsync(fs, ct);

                using (new Reader(tmp)) { } // 壊れていないか確認

                lock (_lock)
                {
                    _reader?.Dispose();
                    _reader = null;
                    File.Move(tmp, UserPath, overwrite: true);
                }
                Open();
                return $"{month:yyyy-MM} 版に更新しました";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        Open();
        throw new InvalidOperationException("ダウンロードに失敗しました。" + (last is null ? "" : " " + last.Message));
    }

    public void Dispose() => _reader?.Dispose();
}
