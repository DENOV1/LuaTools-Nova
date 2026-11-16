// LuaTools Nova — legitimate Steam store pricing via the public appdetails endpoint.
// The original app does not show prices; this is a Nova feature requested in the v1.0.3 test
// round. Uses Steam's public store API (same endpoint Steam's own web store uses), honors the
// user's Steam region when discoverable (registry.vdf "country", the same file the locator reads
// language from), caches per (appid, cc) with a TTL so browsing doesn't hammer the endpoint and
// stale data is never presented as current (entries expire; failures are not cached as prices).
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Infrastructure.Clients;

public sealed record StorePrice(
    string Currency,
    string FinalFormatted,
    string? InitialFormatted,
    int DiscountPercent,
    bool IsFree);

public sealed partial class SteamStoreService
{
    /// <summary>How long a price snapshot may be shown as "current".</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(15));
    private readonly ISteamLocator _locator;
    private readonly ConcurrentDictionary<string, (StorePrice Price, DateTimeOffset At)> _cache = new();
    private readonly ConcurrentDictionary<string, Task<StorePrice?>> _inFlight = new();

    public SteamStoreService(ISteamLocator locator) => _locator = locator;

    /// <summary>Steam country from ~/.steam/registry.vdf ("country" — set by the client from the
    /// account region). Null when unknown → the API then geolocates by IP.</summary>
    public string? SteamCountry
    {
        get
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (string candidate in new[]
                         {
                             Path.Combine(home, ".steam", "registry.vdf"),
                             Path.Combine(home, ".steam", "steam", "registry.vdf"),
                         })
                {
                    if (!File.Exists(candidate)) continue;
                    if (TryReadCountry(File.ReadAllText(candidate), out string cc)) return cc;
                }
            }
            catch { /* unreadable → null */ }
            return null;
        }
    }

    /// <summary>Pure seam (tests): parse a registry.vdf body for the account country.</summary>
    public static bool TryReadCountry(string registryVdfText, out string country)
    {
        country = "";
        var m = CountryRegex().Match(registryVdfText);
        if (!m.Success) return false;
        country = m.Groups[1].Value.ToLowerInvariant();
        return country.Length == 2;
    }

    [GeneratedRegex(@"^[\s]*""[Cc]ountry""\s*""([A-Za-z]{2})""", RegexOptions.Multiline)]
    private static partial Regex CountryRegex();

    /// <summary>Current price for an app, or null when unavailable (region-restricted, network
    /// failure, or the game has no price overview). Never fabricated; never served past TTL.</summary>
    public Task<StorePrice?> GetPriceAsync(long appId, CancellationToken ct = default)
    {
        string cc = SteamCountry ?? "";
        string key = $"{appId}:{cc}";
        if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            return Task.FromResult<StorePrice?>(hit.Price);

        return _inFlight.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try
            {
                string url = $"{AppConfig.SteamStoreSearchUrl.Split("/api/")[0]}/api/appdetails?appids={appId}&filters=price_overview,is_free"
                             + (cc.Length == 0 ? "" : $"&cc={cc}&l=english");
                using var res = await _http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode)
                {
                    NovaLog.Warn("price", $"appdetails HTTP {(int)res.StatusCode} for {appId}");
                    return null;
                }
                string json = await res.Content.ReadAsStringAsync(ct);
                var parsed = ParseAppDetails(json, appId);
                if (parsed is null)
                {
                    NovaLog.Info("price", $"no price overview for {appId} (free/unavailable/region-restricted)");
                    return null;
                }
                return Remember(key, parsed);
            }
            catch (Exception ex)
            {
                NovaLog.Warn("price", $"appdetails failed for {appId}: {ex.Message}");
                return null;
            }
            finally { ((ICollection<KeyValuePair<string, Task<StorePrice?>>>)_inFlight).Remove(new(key, _inFlight.TryGetValue(key, out var t) ? t : null!)); }
        }));
    }

    /// <summary>Pure seam (tests): parse an appdetails JSON body into a StorePrice.</summary>
    public static StorePrice? ParseAppDetails(string json, long appId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId.ToString(), out var entry)
            || entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("success", out var okElem) || okElem.GetBoolean() != true
            || !entry.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("is_free", out var freeElem) && freeElem.GetBoolean())
            return new StorePrice("USD", "Free", null, 0, true);

        if (!data.TryGetProperty("price_overview", out var po) || po.ValueKind != JsonValueKind.Object)
            return null; // no price overview → free weekend / N/A / region-restricted

        string currency = po.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
        string final = po.TryGetProperty("final_formatted", out var ff) ? ff.GetString() ?? "" : "";
        string? initial = po.TryGetProperty("initial_formatted", out var iff) ? iff.GetString() : null;
        int discount = po.TryGetProperty("discount_percent", out var dp) ? dp.GetInt32() : 0;
        if (final.Length == 0) return null;

        return new StorePrice(currency, final,
            discount > 0 && !string.IsNullOrEmpty(initial) ? initial : null, discount, false);
    }

    private StorePrice Remember(string key, StorePrice price)
    {
        _cache[key] = (price, DateTimeOffset.UtcNow);
        return price;
    }

    /// <summary>Tests only.</summary>
    public void ResetForTests() { _cache.Clear(); _inFlight.Clear(); }
}
