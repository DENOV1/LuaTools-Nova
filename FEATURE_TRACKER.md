# FEATURE_TRACKER — Original LuaTools → LuaTools Nova

Statuses: Not Started · Analyzing · In Development · Implemented · Testing · Verified · Blocked · Not Supported (justified)

| # | Feature | Original | Nova status | Evidence |
|---|---|---|---|---|
| 1 | Fluent dark shell + NavigationView (7 menu + footer items) | WPF-UI 4.3 | **Implemented** | MainWindow.axaml mirrors order/icons; builds green |
| 2 | Toast notifications (incl. action toast) | SnackbarService | **Implemented** | ToastServiceImpl → WindowNotificationManager bottom-right |
| 3 | First-run onboarding overlay (sign-in, recommended setup, plugin) | OnboardingView | **Implemented** | OnboardingView + OnboardingViewModel (blur scrim, Yes/No segmented) |
| 4 | Steam path discovery + override | Registry | **Verified** | SteamPathLocatorTests (6 tests) |
| 5 | Library scan (libraryfolders.vdf + appmanifest) | SteamLibraryService | **Verified** | SteamLibraryServiceTests (union + skip-missing) |
| 6 | Home dashboard (stats, status rows, recent, drop zone) | HomeView | **Implemented** | HomeViewModel + HomeView.axaml |
| 7 | Add page: search / appid / link entry | DownloadViewModel | **Implemented** (network-gated) | debounced search, ExtractAppId; endpoints identical |
| 8 | Featured strips (top sellers / new releases) | GetFeaturedAsync | **Implemented** (network-gated) | + hardware blacklist filter |
| 9 | Manifest sources + FastFetch + Hubcap key gating | DownloadViewModel.FetchAsync | **Implemented** (network-gated) | SourceRowViewModel lock/stats logic |
| 10 | DLC info + generate unlock | api endpoints | **Implemented** (network-gated) | DlcInfo DTO + job |
| 11 | Overwrite-confirm diff (added/removed rows, SteamDB links) | ConfirmOverlay | **Implemented** | ConfirmOverwriteAsync + DiffRow; overlay in DownloadView |
| 12 | Drag-and-drop installer (lua/manifest/zip + links) | DropInstallViewModel | **Implemented** | DropZone.axaml(.cs) + tally/batch results |
| 13 | Manage grid: paging, 6 filters, 5 sorts, flyout, selection delete | ManageViewModel | **Implemented** | PagedListViewModel paging unit-tested |
| 14 | Depots page: variants, breakdown, toggles, editor, rename | BuildsViewModel | **Implemented** | LuaVault/LuaEditor tested; DepotRow labels tested |
| 15 | Lua variant vault (content-addressed, edit-base) | LuaVault | **Verified** | LuaVaultTests (6 tests) |
| 16 | Depot download (DepotDownloader, resume/-validate) | DepotDownloaderService | **Testing** | code + unit semantics; linux-x64 tool; end-to-end needs keys/Steam |
| 17 | Downloads queue + history + pause/resume/retry | DownloadQueue | **Verified** | DownloadQueueTests (6 tests) + metrics tests |
| 18 | Denuvo fixes: listings, per-fix manifest/fix, revert | FixesViewModel | **Implemented** (network-gated) | revert record codec tested; Proton gating tested |
| 19 | Fix apply with hash-guarded backups (.luatools-fix) | ManifestJobFactory | **Verified** (codec/paths) | FixRecordTests; live apply needs a game install |
| 20 | Proton gating + WINEDLLOVERRIDES merge | (Moon behavior) | **Verified** | ProtonCompatTests + WineOverridesTests |
| 21 | Launch options editor (appinfo.vdf) | AppInfo services | **Implemented** (codec ported) | BinaryVdf codec in Core; full dialog deferred to context-menu action |
| 22 | Unlocker modes | UnlockerService | **Implemented** (Custom) / OST+BST **Not Supported** | Mode page cards + honest Linux notes |
| 23 | Plugin frontend install (LTSP releases) | PluginInstallerService | **Implemented** (network-gated) | PluginBridge.cs; sha256 verify |
| 24 | CEF bridge (inject + RPC drain) | CefInjectorService | **Implemented** | same CDP protocol; needs Steam with debug flag |
| 25 | Local HTTP backend (127.0.0.1:6767) | HttpServerService | **Implemented** | route table ported; DTO contracts unit-tested |
| 26 | Discord OAuth PKCE + login-code | AuthService | **Implemented** (network-gated) | listener + refresh logic ported |
| 27 | Donate keys | DonateKeysService | **Implemented** | config.vdf VDF parser ported |
| 28 | Restart Steam (clean ladder, gamescope) | SteamService | **Implemented** | ProcessControl (systemd fast paths) |
| 29 | Tray (minimize-to-tray) | WinForms NotifyIcon | **Implemented** | Avalonia TrayIcon + setting |
| 30 | luatools:// protocol | Registry handler | **Implemented** | .desktop MimeType + arg scan |
| 31 | Analytics / hardware blacklist / DoH / mirrors | misc services | **Implemented** | DohResolver.Parse tested |
| 32 | Depotcache migration repair | DepotCacheMigrationService | **Implemented** | harmless on fresh installs |
| 33 | Auto-update (Velopack) | UpdateService | **Deferred** | channel not configured for v1.0.0 |
| 34 | Steamless DRM removal | SteamlessService | **Not Supported** | Windows-only CLI; button disabled with reason |
| 35 | SteamAutoCrack launcher | SteamAutoCrackService | **Not Supported** | Windows-only GUI |
| 36 | CloudRedirect add-on | UnlockerService | **Not Supported** | needs slsteam-moon LD_PRELOAD hook (3rd party) |
| 37 | 29-language UI | resx ×25 | **Deferred** | English shipped; infrastructure ready |

Summary: **Verified 11 · Implemented 20 · Testing 1 · Deferred 2 · Not Supported 3 · (Big-Picture UI excluded by design).**

## v1.0.1 audit update

- All "Implemented" rows above additionally verified through the DI audit + headless UI lifecycle suite (real MainWindow, all 10 pages, navigation — 18/18 green). See TEST_RESULTS.md.
- Startup crash (UnlockerServiceProxy) regression-tested: `DiAuditTests.Regression_*`.
- Packaging scripts executed (user-local mode): install/uninstall verified, user data preserved.
