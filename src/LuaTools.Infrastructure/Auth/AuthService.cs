// Derived from madoiscool/LuaTools (MIT) — AuthService.cs + DonateKeysService.cs + AnalyticsService.cs.
// Auth storage: chmod-600 JSON (Linux) replaces DPAPI.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using LuaTools.Core.Downloads;
using LuaTools.Core.Models;
using LuaTools.Core.Services;
using LuaTools.Infrastructure.Clients;
using LuaTools.Infrastructure.Network;

namespace LuaTools.Infrastructure.Auth;

public sealed class AuthException(string message) : Exception(message);

/// <summary>Supabase Discord OAuth PKCE + bot-code sign-in + token refresh.</summary>
public sealed partial class AuthService : IAccessTokenSource
{
    private readonly string _authFile;
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(30));
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;

    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }
    public string? AvatarUrl { get; private set; }

    public bool IsSignedIn => _refreshToken is not null;
    public bool IsGuest => !IsSignedIn;
    public bool IsBotProvisioned =>
        IsSignedIn && Email?.EndsWith(AppConfig.BotAccountEmailDomain, StringComparison.OrdinalIgnoreCase) == true;

    public event Action? AuthStateChanged;

    public AuthService(string dataDir)
    {
        _authFile = Path.Combine(dataDir, "auth.json");
        TryMakePrivate(_authFile);
    }

    private static void TryMakePrivate(string path)
    {
        try
        {
            if (File.Exists(path) && !OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("chmod", $"600 -- '{path.Replace("'", "'\\''")}'")
                { CreateNoWindow = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
            }
        }
        catch { /* best effort */ }
    }

    public async Task<bool> InitializeAsync()
    {
        StoredAuth? stored = LoadStored();
        if (stored is null || string.IsNullOrEmpty(stored.RefreshToken)) return false;

        _accessToken = stored.AccessToken;
        _refreshToken = stored.RefreshToken;
        _expiresAt = stored.ExpiresAt;
        DisplayName = stored.DisplayName;
        Email = stored.Email;
        AvatarUrl = stored.AvatarUrl;

        if (_expiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            AuthStateChanged?.Invoke();
            return true;
        }
        try
        {
            await RefreshAsync();
            AuthStateChanged?.Invoke();
            return true;
        }
        catch
        {
            ClearSession();
            AuthStateChanged?.Invoke();
            return false;
        }
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        string verifier = CreateCodeVerifier();
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        string authorizeUrl =
            $"{AppConfig.SupabaseUrl}/auth/v1/authorize?provider=discord" +
            $"&redirect_to={Uri.EscapeDataString(AppConfig.OAuthCallbackUrl)}" +
            $"&code_challenge={challenge}&code_challenge_method=s256";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{AppConfig.OAuthCallbackPort}/");
        listener.Start();

        try
        {
            SystemIntegrationHelpers.OpenUrlExternal(authorizeUrl);
        }
        catch
        {
            listener.Stop();
            throw new AuthException("Couldn't open a browser for sign-in.");
        }

        string code;
        try
        {
            code = await WaitForCallbackAsync(listener, ct);
        }
        finally
        {
            listener.Stop();
        }

        var session = await ExchangeCodeAsync(code, verifier, ct);
        ApplySession(session);
        AuthStateChanged?.Invoke();
    }

    public async Task SignInWithCodeAsync(string code, CancellationToken ct = default)
    {
        var redeemReq = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.ApiBaseUrl}/api/auth/code/redeem")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code = code.Trim().ToUpperInvariant() }),
                Encoding.UTF8, "application/json"),
        };

        var redeemRes = await _http.SendAsync(redeemReq, ct);
        string redeemBody = await redeemRes.Content.ReadAsStringAsync(ct);
        if (!redeemRes.IsSuccessStatusCode)
            throw new AuthException(redeemRes.StatusCode switch
            {
                HttpStatusCode.Gone => "Code expired. Run /login again.",
                HttpStatusCode.NotFound => "Invalid or unknown code.",
                HttpStatusCode.BadRequest => "Invalid or unknown code.",
                _ => "Server error. Try again.",
            });

        var redeem = JsonSerializer.Deserialize<CodeRedeemResponse>(redeemBody);
        if (redeem is null || string.IsNullOrEmpty(redeem.Token))
            throw new AuthException("Server error. Try again.");

        var session = await VerifyMagicTokenAsync(redeem.Token, ct);
        ApplySession(session);
        AuthStateChanged?.Invoke();
    }

    private async Task<SupabaseSession> VerifyMagicTokenAsync(string tokenHash, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/verify")
        {
            Content = JsonContent.Create(new { type = "magiclink", token_hash = tokenHash }),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException("Server error. Try again.");

        return JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException("Server error. Try again.");
    }

    private static async Task<string> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new AuthException("Sign-in timed out. No response from the browser.");
            }

            string? code = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "").Get("code");
            string? error = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "").Get("error_description");

            if (code is null && error is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            bool ok = code is not null;
            byte[] page = Encoding.UTF8.GetBytes(ResultPage(ok, error));
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = page.Length;
            await ctx.Response.OutputStream.WriteAsync(page, timeout.Token);
            ctx.Response.Close();

            if (!ok) throw new AuthException(error ?? "Sign-in was denied.");
            return code!;
        }
    }

    private async Task<SupabaseSession> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=pkce")
        {
            Content = JsonContent.Create(new { auth_code = code, code_verifier = verifier }),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Token exchange failed ({(int)res.StatusCode}): {body}");

        return JsonSerializer.Deserialize<SupabaseSession>(body)
               ?? throw new AuthException("Token exchange returned an empty session.");
    }

    public async Task<string> GetValidAccessTokenAsync()
    {
        if (_refreshToken is null) throw new AuthException("Not signed in.");

        if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            await _refreshLock.WaitAsync();
            try
            {
                if (_expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
                    await RefreshAsync();
            }
            finally { _refreshLock.Release(); }
        }
        return _accessToken!;
    }

    private async Task RefreshAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{AppConfig.SupabaseUrl}/auth/v1/token?grant_type=refresh_token")
        {
            Content = JsonContent.Create(new { refresh_token = _refreshToken }),
        };
        req.Headers.Add("apikey", AppConfig.SupabaseAnonKey);

        var res = await _http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new AuthException($"Session refresh failed ({(int)res.StatusCode}).");

        var session = JsonSerializer.Deserialize<SupabaseSession>(body)
                      ?? throw new AuthException("Session refresh returned an empty session.");
        ApplySession(session);
    }

    public void SignOut()
    {
        ClearSession();
        AuthStateChanged?.Invoke();
    }

    private void ApplySession(SupabaseSession session)
    {
        _accessToken = session.AccessToken;
        _refreshToken = session.RefreshToken;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresIn);

        if (session.User is not null)
        {
            var meta = session.User.Metadata;
            DisplayName = meta?.CustomClaims?.GlobalName ?? meta?.FullName ?? meta?.Name ?? session.User.Email;
            Email = session.User.Email;
            AvatarUrl = meta?.AvatarUrl;
        }

        SaveStored(new StoredAuth
        {
            RefreshToken = _refreshToken,
            AccessToken = _accessToken,
            ExpiresAt = _expiresAt,
            DisplayName = DisplayName,
            Email = Email,
            AvatarUrl = AvatarUrl,
        });
    }

    private void ClearSession()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = default;
        DisplayName = Email = AvatarUrl = null;
        try { File.Delete(_authFile); } catch { /* best effort */ }
    }

    private StoredAuth? LoadStored()
    {
        try
        {
            if (!File.Exists(_authFile)) return null;
            return JsonSerializer.Deserialize<StoredAuth>(File.ReadAllText(_authFile));
        }
        catch { return null; }
    }

    private void SaveStored(StoredAuth auth)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_authFile)!);
            File.WriteAllText(_authFile, JsonSerializer.Serialize(auth));
            TryMakePrivate(_authFile);
        }
        catch { /* locked/denied → stay signed in for this session */ }
    }

    private static string CreateCodeVerifier()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(48);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ResultPage(bool ok, string? error) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>LuaTools Nova</title>
        <style>
          body { background:#0b0b12; color:#e5e7eb; font-family:sans-serif;
                 display:flex; align-items:center; justify-content:center; height:100vh; margin:0; }
          .card { text-align:center; padding:2.5rem 3rem; background:#14141c;
                  border:1px solid rgba(255,255,255,.08); border-radius:14px; }
          h1 { font-size:1.3rem; margin:0 0 .5rem; color:{{(ok ? "#a78bfa" : "#f87171")}}; }
          p { color:#9ca3af; font-size:.95rem; margin:0; }
        </style></head>
        <body><div class="card">
          <h1>{{(ok ? "Signed in!" : "Sign-in failed")}}</h1>
          <p>{{(ok ? "You can close this tab and return to LuaTools Nova." : WebUtility.HtmlEncode(error ?? "Please try again from the app."))}}</p>
        </div></body></html>
        """;
}

/// <summary>URL opener for flows that run before DI exists.</summary>
public static class SystemIntegrationHelpers
{
    public static void OpenUrlExternal(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
        {
            // Strip the Steam runtime env so spawned GUI binaries link against system libs.
            var psi = new ProcessStartInfo("/bin/sh",
                $"-c \"unset LD_LIBRARY_PATH LD_PRELOAD LD_AUDIT STEAM_RUNTIME_LIBRARY_PATH STEAM_ZENITY; exec xdg-open {SingleQuote(url)}\"")
            { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.Dispose();
        }
    }

    public static string SingleQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>Scrubs config.vdf DecryptionKeys and donates them to the community pool.</summary>
public sealed partial class DonateKeysService
{
    private const string DonationUrl = AppConfig.ManifestBackendUrl + "/donatekeys/send";

    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(15));
    private readonly LuaTools.Core.Persistence.SettingsService _settings;
    private readonly ISteamLocator _steam;
    private readonly LuaTools.Core.Persistence.CacheService _cache;

    public DonateKeysService(LuaTools.Core.Persistence.SettingsService settings, ISteamLocator steam,
        LuaTools.Core.Persistence.CacheService cache)
    {
        _settings = settings;
        _steam = steam;
        _cache = cache;
    }

    [GeneratedRegex(@"^\d{1,10}$")]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9]{64}$")]
    private static partial Regex KeyRegex();

    public async Task SendPendingKeysIfEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            if (!_settings.DonateKeys) return;

            string? root = _steam.EffectivePath;
            if (root is null) return;
            string configPath = Path.Combine(root, "config", "config.vdf");
            if (!File.Exists(configPath)) return;

            var pairs = ExtractKeys(File.ReadAllText(configPath));
            if (pairs.Count == 0) return;

            var donated = new HashSet<string>(_cache.GetDonatedAppIds());
            var fresh = pairs.Where(p => !donated.Contains(p.appid)).ToList();
            if (fresh.Count == 0) return;

            string payload = string.Join(",", fresh.Select(p => $"{p.appid}:{p.key}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, DonationUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.DonateKeysUserAgent);

            var res = await _http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK) return;

            donated.UnionWith(fresh.Select(p => p.appid));
            _cache.SaveDonatedAppIds(donated);
        }
        catch { /* background task, never surface errors */ }
    }

    internal static List<(string appid, string key)> ExtractKeys(string content)
    {
        var root = ParseVdf(content);
        var result = new List<(string, string)>();
        Walk(root, result);
        return result;

        static void Walk(Dictionary<string, object> node, List<(string, string)> result)
        {
            foreach (var (k, v) in node)
            {
                if (v is not Dictionary<string, object> child) continue;
                if (child.TryGetValue("DecryptionKey", out var dk) && dk is string key)
                {
                    if (IsValidPair(k, key)) result.Add((k, key));
                }
                else
                {
                    Walk(child, result);
                }
            }
        }
    }

    private static bool IsValidPair(string appid, string key) =>
        AppIdRegex().IsMatch(appid) && KeyRegex().IsMatch(key);

    private static Dictionary<string, object> ParseVdf(string content)
    {
        var root = new Dictionary<string, object>();
        var stack = new Stack<Dictionary<string, object>>();
        stack.Push(root);
        string? pendingKey = null;

        int pos = 0, len = content.Length;
        while (pos < len)
        {
            char c = content[pos];

            if (c == '/' && pos + 1 < len && content[pos + 1] == '/')
            {
                int nl = content.IndexOf('\n', pos);
                pos = nl < 0 ? len : nl + 1;
            }
            else if (c == '"')
            {
                int end = content.IndexOf('"', pos + 1);
                if (end < 0) break;
                string token = content[(pos + 1)..end];
                pos = end + 1;

                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek()[pendingKey] = token;
                    pendingKey = null;
                }
            }
            else if (c == '{')
            {
                if (pendingKey is not null)
                {
                    var child = new Dictionary<string, object>();
                    stack.Peek()[pendingKey] = child;
                    stack.Push(child);
                    pendingKey = null;
                }
                pos++;
            }
            else if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                pos++;
            }
            else
            {
                pos++;
            }
        }

        return root;
    }
}

/// <summary>Anonymous app-launch analytics via Umami. Best-effort fire-and-forget.</summary>
public sealed class AnalyticsService
{
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(10));

    private static string Version => "1.0.0";

    public async Task TrackAppLaunchAsync(CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                type = "event",
                payload = new
                {
                    website = AppConfig.UmamiWebsiteId,
                    hostname = AppConfig.UmamiHostname,
                    url = $"/launch/{Version}",
                    title = "App Launch",
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{AppConfig.UmamiHost}/api/send")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            await _http.SendAsync(req, ct);
        }
        catch { /* telemetry must never affect the app */ }
    }
}
