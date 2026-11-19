using System.Collections.Concurrent;
using System.Net;

namespace NetWatch.Services;

/// <summary>
/// DNS-Client の ETW で名前が取れなかった IP 用の逆引き（PTR）キャッシュ。
/// 結果は非同期で埋まり、次の画面更新で反映される。
/// </summary>
public sealed class ReverseDns
{
    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly SemaphoreSlim _gate = new(4);

    public string? TryGet(string ip)
    {
        if (_cache.TryGetValue(ip, out var name)) return name;
        _cache[ip] = null; // 問い合わせ中
        _ = LookupAsync(ip);
        return null;
    }

    private async Task LookupAsync(string ip)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var entry = await Dns.GetHostEntryAsync(ip, cts.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != ip)
                _cache[ip] = entry.HostName;
        }
        catch { /* 逆引きなし */ }
        finally { _gate.Release(); }
    }
}
