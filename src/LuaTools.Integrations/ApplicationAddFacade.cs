// LuaTools Nova — facade the HTTP backend uses to reach the headless add flow + Fixes page,
// implemented by the Application layer (kept as an interface here to avoid a layering cycle).
using LuaTools.Core.Downloads;

namespace LuaTools.Integrations;

public interface IApplicationAddFacade
{
    Task StartPluginAddAsync(long appId);
    Task DownloadSourceByNameAsync(string name);
    Task OpenForAppIdAsync(long appId);
    PluginAddStateDto? GetAddState(long appId);
}

/// <summary>Serializable projection of the headless add state for the plugin popup.</summary>
public sealed class PluginAddStateDto
{
    public long AppId { get; set; }
    public bool Checking { get; set; }
    public bool FastFetch { get; set; }
    public bool SourcesLoaded { get; set; }
    public List<SourceDto> Sources { get; set; } = new();
    public string? InstallStatus { get; set; }
    public bool InstallFailed { get; set; }
    public string? Error { get; set; }

    public sealed class SourceDto
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Available { get; set; }
        public bool CanDownload { get; set; }
        public bool Locked { get; set; }
        public bool NeedsKey { get; set; }
        public string? Stats { get; set; }
        public bool Downloading { get; set; }
        public double Progress { get; set; }
        public bool Indeterminate { get; set; }
    }
}

/// <summary>Constructor-facing adapter so registrations stay terse; resolved from DI by the app host.</summary>
public sealed class ApplicationAddFacade(Func<IApplicationAddFacade> factory) : IApplicationAddFacade
{
    private IApplicationAddFacade Resolve() => factory();
    public Task StartPluginAddAsync(long appId) => Resolve().StartPluginAddAsync(appId);
    public Task DownloadSourceByNameAsync(string name) => Resolve().DownloadSourceByNameAsync(name);
    public Task OpenForAppIdAsync(long appId) => Resolve().OpenForAppIdAsync(appId);
    public PluginAddStateDto? GetAddState(long appId) => Resolve().GetAddState(appId);
}
