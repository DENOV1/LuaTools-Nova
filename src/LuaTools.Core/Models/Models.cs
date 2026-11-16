// Derived from madoiscool/LuaTools (MIT) — Models (ApiModels.cs / FixModels.cs / ModeModels.cs subset).
using System.Text.Json.Serialization;

namespace LuaTools.Core.Models;

public sealed class SteamSearchResult
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
}

public sealed class SteamStoreSearchResponse
{
    [JsonPropertyName("items")] public List<SteamStoreItem> Items { get; set; } = new();
}

public sealed class SteamStoreItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tiny_image")] public string? TinyImage { get; set; }
}

public sealed class SteamFeaturedResponse
{
    [JsonPropertyName("top_sellers")] public SteamFeaturedCategory? TopSellers { get; set; }
    [JsonPropertyName("new_releases")] public SteamFeaturedCategory? NewReleases { get; set; }
}

public sealed class SteamFeaturedCategory
{
    [JsonPropertyName("items")] public List<SteamFeaturedItem> Items { get; set; } = new();
}

public sealed class SteamFeaturedItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("large_capsule_image")] public string? LargeCapsuleImage { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
}

public sealed class GameDetails
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("baseAppId")] public string? BaseAppId { get; set; }
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = new();
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }

    [JsonIgnore] public bool IsDlc => string.Equals(Type, "dlc", StringComparison.OrdinalIgnoreCase);
}

public sealed class DlcDepot
{
    [JsonPropertyName("depotId")] public string DepotId { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("oslist")] public string? OsList { get; set; }
    [JsonPropertyName("included")] public bool Included { get; set; }

    [JsonIgnore]
    public string Meta
    {
        get
        {
            var parts = new List<string> { Language ?? "default" };
            if (!string.IsNullOrEmpty(OsList)) parts.Add(OsList);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class DlcInfo
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("depotCount")] public int DepotCount { get; set; }
    [JsonPropertyName("haveCount")] public int HaveCount { get; set; }
    [JsonPropertyName("missingCount")] public int MissingCount { get; set; }
    [JsonPropertyName("depots")] public List<DlcDepot> Depots { get; set; } = new();
}

public sealed class HubcapStats
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("daily_usage")] public int DailyUsage { get; set; }
    [JsonPropertyName("daily_limit")] public int DailyLimit { get; set; }
    [JsonPropertyName("can_make_requests")] public bool CanMakeRequests { get; set; }
    [JsonPropertyName("api_key_expires_at")] public string? ApiKeyExpiresAt { get; set; }
}

public sealed class HubcapManifestStatus
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("manifest_file_exists")] public bool ManifestFileExists { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed record StandardUsage(int Used, int Limit);

public sealed class SupporterStatus
{
    [JsonPropertyName("isSupporter")] public bool IsSupporter { get; set; }
}

public sealed class CodeRedeemResponse
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public sealed class SupabaseSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("user")] public SupabaseUser? User { get; set; }
    public long ExpiresAtMs { get; set; }
}

public sealed class SupabaseUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("user_metadata")] public UserMetadata? Metadata { get; set; }
}

public sealed class UserMetadata
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("custom_claims")] public CustomClaims? CustomClaims { get; set; }
}

public sealed class CustomClaims
{
    [JsonPropertyName("global_name")] public string? GlobalName { get; set; }
}

/// <summary>Persisted auth state (chmod-600 JSON on Linux, replacing DPAPI).</summary>
public sealed class StoredAuth
{
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>Per-source UI metadata (mirrors the website's source-meta).</summary>
public static class SourceMeta
{
    public sealed record Meta(string? DisplayName = null, string? DiscordUrl = null, bool RequiresUserKey = false);

    public static readonly Dictionary<string, Meta> All = new()
    {
        ["Ryuu"] = new(DiscordUrl: "https://discord.gg/manifests"),
        ["TwentyTwo Cloud"] = new(DiscordUrl: "https://discord.gg/RrukXPyv5b"),
        ["Sushi"] = new(DiscordUrl: "https://discord.gg/hMdv5dQhcN"),
        ["Skyflare"] = new(DiscordUrl: "https://discord.gg/luatools"),
        ["Sadie (Morrenus)"] = new(DisplayName: "Sadie (Hubcap)", DiscordUrl: "https://discord.gg/hubcapsmanifest", RequiresUserKey: true),
    };

    public static Meta Get(string name) => All.TryGetValue(name, out var m) ? m : new Meta();
}

// ── Denuvo fixes ─────────────────────────────────────────────────

public sealed class DenuvoListingsResponse
{
    [JsonPropertyName("games")] public List<DenuvoGameListing> Games { get; set; } = new();
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = new();
}

public sealed class DenuvoGameListing
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixCount")] public int FixCount { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = new();
}

public sealed class DenuvoTag
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
}

public sealed class DenuvoFixesResponse
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image")] public string? HeaderImage { get; set; }
    [JsonPropertyName("fixes")] public List<DenuvoFix> Fixes { get; set; } = new();
}

public sealed class DenuvoFix
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("tags")] public List<DenuvoTag> Tags { get; set; } = new();
    [JsonPropertyName("hasManifest")] public bool HasManifest { get; set; }
    [JsonPropertyName("hasFix")] public bool HasFix { get; set; }
    [JsonPropertyName("manifestFilename")] public string? ManifestFilename { get; set; }
    [JsonPropertyName("fixFilename")] public string? FixFilename { get; set; }
    [JsonPropertyName("createdAt")] public string? CreatedAt { get; set; }
}

public sealed class DenuvoDownloadResponse
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

public sealed class DenuvoFixRecord
{
    [JsonPropertyName("appId")] public long AppId { get; set; }
    [JsonPropertyName("fixId")] public string FixId { get; set; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("files")] public List<DenuvoFixRecordEntry> Files { get; set; } = new();
}

public sealed class DenuvoFixRecordEntry
{
    [JsonPropertyName("relativePath")] public string RelativePath { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("backupPath")] public string? BackupPath { get; set; }
    [JsonPropertyName("hashAfter")] public string? HashAfter { get; set; }
    [JsonPropertyName("hashBefore")] public string? HashBefore { get; set; }
}

public sealed class AppliedFixIndex
{
    [JsonPropertyName("entries")] public List<AppliedFixIndexEntry> Entries { get; set; } = new();
}

public sealed class AppliedFixIndexEntry
{
    [JsonPropertyName("appId")] public long AppId { get; set; }
    [JsonPropertyName("fixId")] public string FixId { get; set; } = "";
    [JsonPropertyName("gameName")] public string GameName { get; set; } = "";
    [JsonPropertyName("installDir")] public string InstallDir { get; set; } = "";
    [JsonPropertyName("appliedAt")] public string AppliedAt { get; set; } = "";
    [JsonPropertyName("fileCount")] public int FileCount { get; set; }
}

// ── GitHub release DTOs ──────────────────────────────────────────

public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = new();
}

public sealed class GithubAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

// ── Steam depot info (api.steamcmd.net) ──────────────────────────

public sealed record ContentDepot(long Id, long Size, long? DlcAppId, bool IsShared, string? Os, string? Language,
    string? PublicManifestId = null)
{
    public bool IsDlc => DlcAppId is not null;
    public long? FromAppId { get; init; }
}

public sealed record AppDepotInfo(long AppId, IReadOnlyList<ContentDepot> Depots, IReadOnlyList<long> DlcIds,
    IReadOnlyList<string> LaunchExes, string? PublicBuildId = null);

// ── Unlocker modes ───────────────────────────────────────────────

public enum UnlockerMode { Ost, Bst, Custom }
public enum ModeKind { Zip, Manual }
public enum ModeStatus { Unknown, NotInstalled, UpToDate, UpdateAvailable, UserManaged, NotSupported }

public sealed record ModeState(UnlockerMode Mode, ModeStatus Status, bool IsActive, string? LatestVersion);
public sealed record ModeInstallResult(bool Success, string? Error, IReadOnlyList<string> Failed)
{
    public static ModeInstallResult Ok() => new(true, null, Array.Empty<string>());
    public static ModeInstallResult Fail(string error) => new(false, error, Array.Empty<string>());
}

public sealed record DepotSelection(long DepotId, string? ManifestId, string? ManifestPath, long Size)
{
    public long? FromAppId { get; init; }
}

public sealed record DepotRunResult(bool Ok, string? Error);

public enum DepotPhase { Manifest, PreAllocating, Validating, Downloading }
