// LuaTools Nova — async image loading for Avalonia.
//
// Why this exists: the original (WPF) binds `Image Source="{Binding Icon}"` with URL strings —
// WPF converts those automatically. Avalonia does NOT convert string→IImage, so every such
// binding silently failed in Nova (v1.0.1: covers missing app-wide). This replaces those dead
// bindings with:
//   <Image img:ImageLoader.Source="{Binding IconUrl}" ... />
// The loader downloads/caches/decodes off the UI thread, dedups concurrent loads, keeps a
// decoded-image memory cache, disk-caches remote images by URL hash, and exposes readable
// attached state (IsLoading / HasResult) so templates can show loading + fallback states.
// Appid-keyed covers that ViewModels already resolve to local paths (CoverCache, ported from
// the original: dedup + JPEG validation + placeholder rejection + missing-remember) are decoded
// straight from disk.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LuaTools.Infrastructure.Network;

namespace LuaTools.UI.Services;

/// <summary>Loads and decodes images off the UI thread. Remote URLs are disk-cached by URL hash
/// (survives restarts) and decoded results are memory-cached per URL so template recycling never
/// re-downloads. Failures are remembered for the session so a missing cover doesn't retry-storm.</summary>
public sealed class SteamImageCache
{
    private readonly HttpClient _http = AppHttp.Create(TimeSpan.FromSeconds(20));
    private readonly string _urlCacheDir;
    private readonly ConcurrentDictionary<string, Task<IImage?>> _inFlight = new();
    private readonly ConcurrentDictionary<string, IImage?> _decoded = new();
    private readonly ConcurrentDictionary<string, byte> _failedUrls = new();
    private readonly SemaphoreSlim _ioGate = new(4, 4);

    /// <summary>Process-wide instance. App/test harness assigns once at startup.</summary>
    public static SteamImageCache Default { get; set; } =
        new(Path.Combine(NovaPathsDir(), "imgcache"));

    private static string NovaPathsDir() =>
        LuaTools.Application.NovaPaths.DataDir;

    public SteamImageCache(string cacheDir)
    {
        _urlCacheDir = cacheDir;
        try { Directory.CreateDirectory(_urlCacheDir); } catch { /* cache dir optional */ }
    }

    /// <summary>Route by shape: local path → decode file; http(s) URL → disk+memory cached download.</summary>
    public Task<IImage?> LoadAsync(string? source) => source switch
    {
        null or "" => Task.FromResult<IImage?>(null),
        _ when File.Exists(source) => GetOrStart("file:" + source, () => DecodeFileAsync(source)),
        _ when Uri.TryCreate(source, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            => _failedUrls.ContainsKey(source)
                ? Task.FromResult<IImage?>(null)
                : GetOrStart(source, () => DownloadAndDecodeAsync(source)),
        _ => Task.FromResult<IImage?>(null),
    };

    private Task<IImage?> GetOrStart(string key, Func<Task<IImage?>> factory) =>
        _inFlight.GetOrAdd(key, _ => Task.Run(factory));

    /// <summary>Tests only: clear all caches (memory + failed set).</summary>
    public void ResetForTests()
    {
        _inFlight.Clear();
        _decoded.Clear();
        _failedUrls.Clear();
    }

    private async Task<IImage?> DecodeFileAsync(string path)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await Task.Run(() => Bitmap.DecodeToWidth(fs, 600, BitmapInterpolationMode.MediumQuality));
        }
        catch
        {
            _failedUrls[path] = 0;
            return null;
        }
    }

    private async Task<IImage?> DownloadAndDecodeAsync(string url)
    {
        string cachePath = CachePathFor(url);
        try
        {
            byte[] bytes;
            if (File.Exists(cachePath))
            {
                bytes = await File.ReadAllBytesAsync(cachePath);
            }
            else
            {
                await _ioGate.WaitAsync();
                try
                {
                    // Re-check: a concurrent load may have completed the download while we waited.
                    if (File.Exists(cachePath))
                    {
                        bytes = await File.ReadAllBytesAsync(cachePath);
                    }
                    else
                    {
                        using var res = await _http.GetAsync(url);
                        if (!res.IsSuccessStatusCode) return RememberFailure(url);
                        bytes = await res.Content.ReadAsByteArrayAsync();
                        // Same JPEG-magic validation as the original CoverCache: reject error
                        // pages / placeholders instead of caching them.
                        if (!IsLikelyImage(bytes)) return RememberFailure(url);
                        try { await File.WriteAllBytesAsync(cachePath, bytes); }
                        catch { /* cache write is best-effort */ }
                    }
                }
                finally { _ioGate.Release(); }
            }

            var decoded = await DecodeBytesAsync(bytes);
            if (decoded is null) return RememberFailure(url);
            return RememberDecoded(url, decoded);
        }
        catch
        {
            return RememberFailure(url);
        }
    }

    private static async Task<IImage?> DecodeBytesAsync(byte[] bytes)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes);
                return (IImage?)Bitmap.DecodeToWidth(ms, 600, BitmapInterpolationMode.MediumQuality);
            });
        }
        catch { return null; }
    }

    private IImage? RememberDecoded(string key, IImage decoded)
    {
        _decoded[key] = decoded;
        if (_decoded.Count > 256) _decoded.Clear(); // crude cap; decode is cheap vs network
        return decoded;
    }

    private IImage? RememberFailure(string url)
    {
        _failedUrls[url] = 0;
        return null;
    }

    private string CachePathFor(string url)
    {
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))
            .ToLowerInvariant();
        return Path.Combine(_urlCacheDir, hash[..32] + ".img");
    }

    /// <summary>Same validity gate as the original CoverCache: JPEG/PNG/WebP/BMP magic bytes only.</summary>
    internal static bool IsLikelyImage(byte[] b) => b.Length >= 8 && (
        (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                                   // JPEG
        (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||                    // PNG
        (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57) ||    // WEBP (RIFF…W)
        (b[0] == 0x42 && b[1] == 0x4D));                                                     // BMP
}

/// <summary>Attached-property behavior: bind a URL/path to <c>ImageLoader.Source</c> on any Image.
/// Exposes readable attached state for loading/fallback UI:
///   IsVisible="{Binding (img:ImageLoader.IsLoading), RelativeSource={RelativeSource Self}}"
///   IsVisible="{Binding !(img:ImageLoader.HasResult), RelativeSource={RelativeSource Self}}"</summary>
public static class ImageLoader
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(ImageLoader));

    public static readonly AttachedProperty<bool> IsLoadingProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsLoading", typeof(ImageLoader));

    public static readonly AttachedProperty<bool> HasResultProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("HasResult", typeof(ImageLoader));

    static ImageLoader()
    {
        SourceProperty.Changed.AddClassHandler<Image>((image, e) =>
            OnSourceChanged(image, e.NewValue as string));
    }

    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);
    public static string? GetSource(Image image) => image.GetValue(SourceProperty);
    public static bool GetIsLoading(Image image) => image.GetValue(IsLoadingProperty);
    public static bool GetHasResult(Image image) => image.GetValue(HasResultProperty);

    private static void OnSourceChanged(Image image, string? source)
    {
        image.SetValue(HasResultProperty, false);
        image.SetValue(IsLoadingProperty, false);
        image.Source = null;

        if (string.IsNullOrWhiteSpace(source)) return;

        image.SetValue(IsLoadingProperty, true);
        _ = LoadAndApplyAsync(image, source);
    }

    private static async Task LoadAndApplyAsync(Image image, string source)
    {
        try
        {
            var result = await SteamImageCache.Default.LoadAsync(source);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Ignore stale results: the template may have rebound to another item meanwhile.
                if (!string.Equals(image.GetValue(SourceProperty) as string, source, StringComparison.Ordinal)) return;
                image.SetValue(IsLoadingProperty, false);
                if (result is not null)
                {
                    image.Source = result;
                    image.SetValue(HasResultProperty, true);
                }
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (string.Equals(image.GetValue(SourceProperty) as string, source, StringComparison.Ordinal))
                    image.SetValue(IsLoadingProperty, false);
            });
        }
    }
}
