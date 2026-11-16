# Source Code Relationships

## Original LuaTools — load-bearing edges

```
Program ─▶ App(DI) ─▶ every service; App wires ~25 VM delegate pairs (navigation, sign-in, reveal).
MainWindow ─▶ NavigationView(pages), ToastService.Attach, tray, RestartSteam via MainViewModel.
DownloadViewModel ─▶ LuaToolsApiClient/HubcapService, DownloadQueue.Enqueue(ManifestJobFactory job)
                    ─▶ LuaInstaller.InstallZip/InstallLua ─▶ SteamService paths; LuaVault.Capture; CacheService.
ManifestJobFactory ─▶ DepotDownloaderService, SteamDepotInfo (shared depots), ManifestFile (key+size),
                     SteamLibraryService (fix apply), AppliedFixIndexService, CoverCache, ToastService.
BuildsViewModel ─▶ LuaVault, LuaEditor, SteamDepotInfo, DepotDownloaderService, SteamAppInfoCache.
ManageViewModel ─▶ LuaInstaller.EnumerateInstalled, app list/info/cover caches, SteamlessService.
PluginInstallerService ─▶ GithubProxy(LTSP releases), SteamService(stop/start), CefInjectorService.ReloadPluginFilesAsync.
CefInjectorService ◀─▶ HttpServerService (17 routes) ◀─▶ PluginAddService/DownloadQueue/LuaInstaller/CacheService.
UnlockerService ─▶ GithubProxy, AssetHash, SteamService.
AuthService ◀─ Main/Settings/Download/Fixes gating; LuaToolsApiClient bearer.
AppHttp ◀─ every HttpClient; DohResolver inside ConnectCallback; GithubProxy ◀─ all GitHub consumers.
DownloadQueue(pump) ─▶ item.Job.DownloadAsync (bg) → confirm gate (VM overlay TCS) → _installGate → InstallAsync → history.
```

Event spine: `LuaInstaller.Installed(appId)` → Home/Manage/Builds refresh; `LuaVault.VaultChanged` → Builds; `AuthService.AuthStateChanged` → Main/Settings/Onboarding; `UpdateService.UpdateReady` → toast.

## Nova layering (mirrors the original's edges, split for testability)

```
LuaTools.Core            models, lua codec/parser/editor/vault, manifest codec, persistence, download
                         queue model, VDF codec, platform interfaces
LuaTools.Infrastructure  AppHttp/Doh/GithubProxy, api clients, caches, DownloadQueue pump, job factory,
                         depot tool runner, auth, donate, analytics
LuaTools.Linux           ISteamLocator/IProcessControl/ISystemIntegrations implementations, ProtonCompat,
                         WineOverrides
LuaTools.Application     NovaServices composition root + ViewModels — depends only on Core + interfaces
LuaTools.Integrations    CDP bridge + local HTTP backend + plugin frontend installer + add facade
LuaTools.UI              Avalonia app, theme tokens, MainWindow + 10 views, dialogs, toasts
tests/                   Core.Tests, Linux.Tests, Application.Tests, Integration.Tests
```

Dependency direction: UI → Application → (Core + Infrastructure + Linux) → Integrations; Integrations exposes `IApplicationAddFacade` implemented by Application (no cycle). No Linux types leak above the interface line.
