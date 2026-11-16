# Original LuaTools — Architecture Analysis

Source: https://github.com/madoiscool/LuaTools (MIT). All 76 source files under `src/` and the test project were read during analysis.

## Identity

- **Product:** LuaTools — a Windows desktop client for managing Steam "manifest/lua" configurations.
- **Stack:** C# / .NET 8 (`net8.0-windows`), WPF with **WPF-UI 4.3.0** (Fluent controls), CommunityToolkit.Mvvm 8.4.2, Velopack (installer + auto-update), VirtualizingWrapPanel, Markdig.Wpf (markdown), Microsoft.Extensions.Hosting (DI), System.Security.Cryptography.ProtectedData (DPAPI), WinForms (tray icon only).
- **Single project** (`src/LuaToolsGui`) — Views (XAML), ViewModels (`ObservableObject`), services registered in a DI container built in `App`'s constructor.

## Composition root

`Program.Main` (Velopack hooks) → culture selection → registers `luatools://` protocol → single instance via named `Mutex` + `EventWaitHandle`s → `App.OnStartup` builds the host, applies `ModeMigration`, wires ~25 cross-ViewModel navigation delegates, and kicks startup jobs (key donation, analytics, hardware blacklist, depotcache migration).

## Service catalog

| Domain | Services |
|---|---|
| Config | `SettingsService` (settings.json, atomic write + .bak + .corrupt), `CacheService` (cache.json: rate-limit window, donated keys, download history, tool fingerprints, onboarding flag) |
| Steam | `SteamService` (registry discovery, restart), `SteamLibraryService` (libraryfolders.vdf → appmanifest_*.acf), `SteamAppListCache` (Morrenus + Steam GetAppList), `SteamAppInfoCache` (appdetails blobs, 190/200s rolling limiter, child-key tolerance), `SteamDepotInfo` (api.steamcmd.net), `CoverCache` (placeholder fingerprint rejection), `SteamLinkParser`, `HardwareAppIdService` |
| Lua install | `LuaInstaller` (→ `config/stplug-in/<appid>.lua` + `<steam>/depotcache/*.manifest`, AutoUpdateApps comment-out), `LuaFileParser` (addappid/setManifestid grammar), `LuaEditor` (comment toggles), `LuaVault` (content-addressed sha256 variants) |
| Downloads | `DownloadQueue` (IHostedService pump; downloads parallel, installs serialized), `DownloadItem` (3s sliding rate window), `DownloadJob` (delegate record, dedupe key), `ManifestJobFactory` (manifest/DLC/Denuvo/depot/tool jobs; hash-guarded fix backups in `.luatools-fix/`), `HttpFileDownloader` (%TEMP% staging) |
| Network | `AppHttp` (single SocketsHttpHandler; DoH connect callback), `DohResolver` (Cloudflare 1.1.1.1 by IP literal), `GithubProxy` (direct → mirrors), `LuaToolsApiClient`, `HubcapService` |
| Auth | `AuthService` (Supabase Discord OAuth PKCE on localhost:53789, DPAPI-protected auth.dat, bot-code redeem) |
| Modes | `UnlockerService` (OST/BST/Custom; places `dwmapi.dll`, `xinput1_4.dll`, `OpenSteamTool.dll` into the Steam root), `ModeMigration`, `CloudRedirectService` |
| Plugin | `PluginInstallerService` (madoiscool/LTSP releases: `plugin.zip` frontend + `winmm.dll` proxy loader + `.cef-enable-remote-debugging` junction), `CefInjectorService` (CDP on 127.0.0.1:8080, drains RPC queue), `HttpServerService` (127.0.0.1:6767), `PluginAddService` |
| Tools | `DepotDownloaderService` (DepotDownloaderMod.exe, one process per depot, silence watchdog, resume `-validate`), `SteamlessService`, `SteamAutoCrackService`, `DonateKeysService` (config.vdf scraper) |
| AppInfo | `BinaryVdf` + `AppInfoFile` (appinfo.vdf v27/28/29 codec), `LaunchOption`/`LaunchOptionsService`/`LaunchModStore` |

## ViewModels

Main, Home, Download (Add), Manage (paged grid, filters/sorts), Builds (1639 lines: vault variants, depot breakdown, per-depot toggles), Fixes, Downloads, Plugin, Mode, Settings, LaunchOptions, Onboarding, DropInstall, PagedListViewModel<T>.

## Window shell

`ui:FluentWindow` (Mica, rounded, 1080×720, min 760×560) + `ui:TitleBar` + `ui:NavigationView` (left pane 190px; Home/Add/Manage/Depots/Mode/Fixes/Plugin + Restart Steam/Downloads/Settings footer; Discord sign-in pane footer; version label). `SnackbarPresenter` bottom-right. `OnboardingView` blur overlay. Dialogs: LaunchOptionsDialog (820×600), ConfirmOverlay, DropZone.

## Persistence map (Windows)

| Path | Content |
|---|---|
| `%AppData%\LuaToolsGui\settings.json` / `cache.json` / `auth.dat` | settings / bookkeeping / DPAPI session |
| `luavault\<appid>\` | content-addressed lua variants |
| `details\<appid>.json`, `covers\<appid>.jpg` | caches |
| `<Steam>\config\stplug-in\<appid>.lua` | THE live lua Steam reads |
| `<Steam>\depotcache\<depot>_<gid>.manifest` | manifests (sibling of steamapps — NOT under config) |
| `<Steam>\appcache\appinfo.vdf` | launch-option source (373 MB) |

## Limitations observed

1. Hard Windows dependencies (WPF/WinForms/DPAPI/registry/`winmm.dll` DLL hijack/Windows-only tools).
2. Unlocker modes install PE DLLs — meaningless outside Windows.
3. DepotDownloaderMod/SteamAutoCrack/CloudRedirect/Steamless are Windows binaries.
4. Language change requires relaunch.
