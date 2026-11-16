// LuaTools Nova — the facade implementation the HTTP backend uses to reach the real ViewModels
// (registered by the UI host).
using LuaTools.Application.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LuaTools.Application;

public sealed class ApplicationAddFacadeImpl : Integrations.IApplicationAddFacade
{
    private readonly NovaServices _svc;

    public ApplicationAddFacadeImpl(NovaServices svc) => _svc = svc;

    public async Task StartPluginAddAsync(long appId)
    {
        var vm = _svc.Get<DownloadViewModel>();
        if (vm is not null) await vm.StartPluginAddAsync(appId);
    }

    public async Task DownloadSourceByNameAsync(string name)
    {
        var vm = _svc.Get<DownloadViewModel>();
        if (vm is not null) await vm.DownloadSourceByNameAsync(name);
    }

    public async Task OpenForAppIdAsync(long appId)
    {
        var vm = _svc.Get<FixesViewModel>();
        if (vm is not null) await vm.OpenForAppIdAsync(appId);
    }

    public Integrations.PluginAddStateDto? GetAddState(long appId)
    {
        var vm = _svc.Get<DownloadViewModel>();
        if (vm is null)
            return new Integrations.PluginAddStateDto { AppId = appId };

        return new Integrations.PluginAddStateDto
        {
            AppId = appId,
            Checking = vm.IsChecking,
            FastFetch = vm.FastFetch,
            SourcesLoaded = vm.SourcesLoaded,
            Error = vm.Error,
            InstallStatus = vm.InstallStatus,
            InstallFailed = vm.InstallFailed,
            Sources = vm.Sources.Select(s => new Integrations.PluginAddStateDto.SourceDto
            {
                Name = s.Name,
                DisplayName = s.DisplayName,
                Status = s.Status,
                Available = s.IsAvailable,
                CanDownload = s.CanDownload,
                Locked = s.IsLocked,
                NeedsKey = s.NeedsKey,
                Stats = s.StatsText,
                Downloading = s.QueueItem?.IsActive == true,
                Progress = s.QueueItem?.Percent ?? 0,
                Indeterminate = s.QueueItem?.IsIndeterminate ?? true,
            }).ToList(),
        };
    }
}
