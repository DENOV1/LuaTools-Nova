# Complete File Inventory

Statuses: **REUSED** (MIT original code ported to Nova), **ADAPTED** (original concept reimplemented with changes), **REIMPLEMENTED** (behavior knowledge from Moon expressed as original C#), **REPLACED** (Linux-native equivalent), **EXCLUDED** (not carried, reason given), **INFO-ONLY** (read for knowledge).

Method note: the original LuaTools C#/XAML tree (~76 files) and Moon's Lua backend + shell workers were read in full during analysis. Moon's 11k-line frontend JS and ~65-file test suite were analyzed via structured summaries plus targeted verification reads. Inventory below lists what matters to Nova.

## Repository A — madoiscool/LuaTools (MIT)

### Root
| Path | Purpose | Nova disposition |
|---|---|---|
| README.md, CONTRIBUTING.md | product overview, build | INFO-ONLY |
| LICENSE | MIT | basis for Nova's MIT + attribution |
| scripts/check-i18n.py, .github/workflows/i18n-check.yml | resx completeness gate | EXCLUDED (Nova ships English-first) |

### src/LuaToolsGui — shell
| Path | Status |
|---|---|
| LuaToolsGui.csproj | ADAPTED → Nova csproj (Avalonia equivalents; ProtectedData replaced) |
| Program.cs | ADAPTED (mutex→single-window; protocol arg scan kept) |
| App.xaml / App.xaml.cs | REPLACED (Avalonia Fluent dark + own converters); App.xaml.cs logic REUSED |
| MainWindow.xaml(.cs) | REUSED (structure: NavigationView 7+2, snackbar, onboarding overlay, tray) |
| Converters.cs | ADAPTED (Avalonia IValueConverter) |

### Models / Services (all read; disposition)
| Path | Status |
|---|---|
| Models/ApiModels.cs, FixModels.cs, ModeModels.cs | REUSED |
| AppConfig.cs | REUSED |
| SettingsService.cs / CacheService.cs | REUSED (paths→XDG) |
| SteamService.cs | REPLACED (SteamPathLocator + ProcessControl; same public surface) |
| SteamLibraryService.cs | REUSED (+ Moon's config/ union rule) |
| SteamAppListCache.cs / SteamAppInfoCache.cs / SteamDepotInfo.cs / CoverCache.cs | REUSED |
| SteamLinkParser.cs / HardwareAppIdService.cs | REUSED |
| LuaInstaller.cs / LuaFileParser.cs / LuaEditor.cs / LuaVault.cs | REUSED |
| ManifestFile.cs | REUSED |
| Downloads/DownloadQueue.cs, DownloadItem.cs, DownloadJob.cs, DownloadHistory.cs, DownloadProgress.cs, ByteFormat.cs, HttpFileDownloader.cs, ManifestJobFactory.cs | REUSED (dispatcher→Avalonia) |
| DepotDownloaderService.cs | ADAPTED (linux-x64 official tool) |
| SteamlessService.cs / SteamAutoCrackService.cs | ADAPTED (Linux: not runnable → documented, actions disabled) |
| CloudRedirectService.cs | EXCLUDED (Windows exe) |
| UnlockerService.cs | ADAPTED (Custom active; OST/BST documented Not Supported) |
| ModeMigration.cs | REUSED |
| PluginInstallerService.cs | ADAPTED (frontend install kept; loader/junction→Linux CDP flag) |
| CefInjectorService.cs | REUSED (pure .NET) |
| HttpServerService.cs | REUSED |
| PluginAddService.cs | REUSED (as ApplicationAddFacade contract) |
| AuthService.cs | REUSED (DPAPI→chmod-600 file) |
| LuaToolsApiClient.cs / HubcapService.cs | REUSED |
| AppHttp.cs / DohResolver.cs / GithubProxy.cs | REUSED |
| DonateKeysService.cs | REUSED |
| AppliedFixIndexService.cs / DepotCacheMigrationService.cs | REUSED |
| ProtocolService.cs | REPLACED (.desktop MimeType) |
| UpdateService.cs | DEFERRED (Velopack Linux channel) |
| ToastService.cs | REPLACED (Avalonia notification manager) |
| AnalyticsService.cs / AssetHash.cs / FileHash.cs / PluginLog.cs | REUSED |
| AppInfo/BinaryVdf.cs, AppInfoFile.cs, LaunchOption.cs, LaunchOptionsService.cs, LaunchModStore.cs | REUSED |
| Resources/Strings.resx | REUSED concept (strings inline in views; satellite resx infrastructure deferred) |
| Resources/Strings.*.resx ×24 | EXCLUDED (v1 English; documented) |

### ViewModels (all read)
Main, Home, Download, Manage, Builds, Fixes, Downloads, Plugin, Mode, Settings, LaunchOptions, Onboarding, DropInstall, PagedListViewModel — **REUSED** with dialog-service and Dispatcher swaps.

### tests/LuaToolsGui.Tests — INFO-ONLY (Nova writes its own suites mirroring the style).

## Repository B — swwayps/luatools-moon (AGPL-3.0) — knowledge extraction only

| Path | Type | Status |
|---|---|---|
| LICENSE (AGPL-3.0) | legal | constrains Nova: no code reuse |
| install.sh (3,148 lines) | bash | REIMPLEMENTED (discovery/bootstrap/immutability/restart knowledge; independent C#) |
| uninstall.sh / diagnose.sh | bash | INFO-ONLY (paths inventoried) |
| scripts/build.sh, check-release-version.sh, refresh-lua-tools-fix-index.py | tooling | INFO-ONLY |
| scripts/test-*.sh/lua/js ×~60, tests/ ×5 | tests | INFO-ONLY (summarized) |
| backend/main.lua + all modules (api_manifest, downloads, smart_merge, fixes, fix_overlays, launcherfix, launchopts, protoncompat, steam_utils, steamlang, slsteam, lua_tools_*, onlinefix, auto_update, settings/*, guard, source_limits, http_client, paths, config) | lua | REIMPLEMENTED (behavior only) |
| backend/update.json, api.defaults.json | json | INFO-ONLY |
| backend/locales/*.json ×27 | i18n | INFO-ONLY |
| backend/bin/7zz | binary | EXCLUDED (Nova shells out to system unzip/7z) |
| public/luatools.js (11,135 lines) | js | INFO-ONLY (Nova fetches upstream plugin.zip at runtime, does not fork it) |
| public/steamdb-webkit.css, themes/*.css ×12 | css | INFO-ONLY |
