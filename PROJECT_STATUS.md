# PROJECT_STATUS — LuaTools Nova

**Status: v1.0.3 — second Mint 22.3 test round fixed at root-cause level. 136/136 automated tests green. Manage-delete deadlock killed (async dialogs), source failures made honest, FastFetch setting leak fixed, Restart Steam wired Linux-native, post-install guidance added, Steam store prices implemented. Release v1.0.3 published as pre-release.**

### v1.0.3 (Mint 22.3 test round 2 → fixes)

| Report | Root cause (traced) | Fix | Regression test |
|---|---|---|---|
| Fetch: game not in Steam; Replace dialog on 2nd attempt | Install **did** work (that's why fetch 2 saw the existing lua) — but Steam only picks up stplug-in luas after a restart, and v1.0.2 never said so nor offered a restart. Second-fetch Replace + "same content" = correct existing-entry detection | Post-install status now says "Restart Steam to see it in your library (Steam root: …)" + Restart Steam button on the Add page | `InstallLua_ThenReadInstalled_FindsExisting_AtSameRoot` |
| Fetch config interface missing; "proceeds directly" | `ProtocolInstall` set `FastFetch=true`, whose setter **persists to settings.json** — one protocol launch permanently switched every later Fetch to silent auto-download, skipping the source/confirm interface (the original shows the same source cards + Replace dialog; Nova does too when FastFetch is off) | Protocol flow saves/restores the user's FastFetch choice | `ProtocolInstall_Restores_FastFetch_Setting` |
| "No available sources found" everywhere | `CheckSourcesAsync` silently returned an empty dict on any HTTP/network failure → a flaky manifest backend was indistinguishable from genuinely no sources (the original has the same silent-empty behavior; Nova improves on it) | Backend/network failures now raise distinct errors; `SourceDiagnostics` classifies backend-down vs no-listing vs sign-in/key-gated vs all-erroring; NovaLog records outcomes | `SourceDiagnostics_Distinguishes_BackendFailure_From_NoSources` |
| Manage delete freezes the whole app | `DialogServiceImpl.Confirm` did `ConfirmAsync(...).GetAwaiter().GetResult()` **on the UI thread** — blocking on a dialog that itself needs the UI thread = deadlock. Every Confirm/Info in the app had the same bug | `IDialogService` is async end-to-end (`ConfirmAsync`/`InfoAsync`); all 6 call sites ported; delete runs on a worker thread with a re-entry guard, progress state, per-file failure counting, list refresh, success/error toasts | `Manage_DeleteSelected_Deletes_Files_And_Refreshes`, `Manage_DeleteSelected_Reentry_Guarded` |
| Restart Steam button missing | `MainViewModel.RestartSteamCommand` **existed but no view bound it** — and it used the deadlocking dialog | Linux-native restart (ProcessControl: `steam -shutdown` → SIGTERM → SIGKILL, systemd-unit fast path on SteamOS/Bazzite — same ladder as LuaTools Moon's stop_steam) now reachable from Home, Add status area; confirm + running-state + failure reporting | `Home_RestartSteam_Wired_To_ProcessControl` |
| Two Settings buttons / empty screen | The Home "Steam" status row was mis-bound to `OpenSettingsCommand` (a second, pointless Settings entry) | Row is now the Restart Steam control; real Settings stays in the nav footer | headless construction |
| No store prices | Feature absent (original has no prices either) | `SteamStoreService`: public Steam appdetails API, region from `~/.steam/registry.vdf` "country" (falls back to IP geolocation), 30-min TTL cache, in-flight dedup; Add-page details card shows final price, strikethrough original when discounted, "Free", "Price unavailable" — never fabricated | `StorePriceTests` (parse: discounted/plain/free/unavailable + registry country) + live endpoint smoke |

**Moon reference:** its `stop_steam` ladder (graceful `-shutdown` bounded with timeout → SIGTERM → SIGKILL, pgrep gates, bootstrap-symlink checks) and Steam detection assumptions match Nova's `ProcessControl`, which v1.0.3 finally exposes in the UI.

### v1.0.2 (Mint 22.3 test round 1 → fixes)

| Report | Root cause (traced, not guessed) | Fix | Regression test |
|---|---|---|---|
| Add page: covers missing | Avalonia has no string→IImage conversion (original is WPF, which does) — every `Image Source="{Binding …}"` with a URL/path silently failed; search rows had no icon column at all | New `ImageLoader` attached behavior + `SteamImageCache` (disk-cached by URL hash, in-flight dedup, memory cache, magic-byte validation, session-level missing-remember); search rows restored to original `[icon 60×28 | name | appid]` layout | headless MainWindow suite constructs the new templates; image validation unit-tested |
| Add page: title wrong position | Details card had no header image and a different internal layout than the original | Details card restored to original: header art (≤180px, rounded top) → title + DLC chip → appid · genres → Fetch | `DetailsHeaderUrl_TracksSelectedGame` |
| Add page: Fetch disabled | `_details`/`_isChecking` never notified `FetchCommand` → CanExecute never re-queried after async selection (CommunityToolkit commands do not auto-listen) | `[NotifyCanExecuteChangedFor(FetchCommand)]` on both | `FetchCommand_CanExecuteChanged_Fires_WhenDetailsArrive`, `FetchCommand_Disables_WhileChecking_And_ReEnables_After` |
| Fixes page: buttons disabled / broken | Two real causes: (1) `GetDenuvoFixesAsync` null return or exception left a SILENT empty list; (2) `GameInstalled=false` (Steam not discovered → `GetInstallDir` null) disabled Fix buttons with no explanation | Error banner + Retry for load failures; honest "Steam was not detected" banner; hints explain the disabled state | `Fixes_NullResponse_ShowsError_NotSilentEmpty`, `Fixes_ValidResponse_Populates_And_ClearsError`, `Fixes_SteamMissing_IsDeclaredHonestly` |
| Audit: Mode page buttons dead | `ModeCardViewModel.ShowActionButton` was a plain `{ get; set; }` set after binding | `[ObservableProperty]` | `ModeCard_ShowActionButton_IsObservable` |

Command-binding audit (scripted): all 93 VM commands referenced from XAML resolve — no missing bindings app-wide. Live-API shape check: `hasManifest/hasFix` and listings contract match the original exactly.

Last updated: 2026-11-16

## The v1.0.0 startup crash (Linux Mint 22.3) — FIXED

```
System.InvalidOperationException: No service for type
'LuaTools.Application.ViewModels.UnlockerServiceProxy' has been registered.
  at NovaServices.Get<T>() — NovaServices.cs:139
  at ModeViewModel constructor — PageViewModels.cs:565
  at App.OnFrameworkInitializationCompleted() — App.axaml.cs:53
```

**Root cause:** split composition root. The DI container in `NovaServices` registered only domain services; `UnlockerServiceProxy`, `AuthServiceProxy`, `PluginInstallerService`, UI services (`IDialogService`/`IToastService`/`INavigationHost`) and **all 12 ViewModels** were constructed ad-hoc in `App.axaml.cs`, yet ViewModels fetched them via `svc.Get<T>()`. `ModeViewModel`'s constructor was simply the first resolution to hit an unregistered dependency; 14+ more waited behind it (MainWindow resolves 9 page VMs via `Get<T>`; `ApplicationAddFacadeImpl` resolves VMs for the plugin backend; `HomeViewModel` fetches `PluginInstallerService`).

**Fix (architectural, not cosmetic):** `NovaServices` is now the single composition root — it registers every service, every proxy, the integrations layer, logging, and all 12 ViewModels; the UI layer contributes its three implementations through a `configure` callback before the container builds; the container builds with **ValidateOnBuild + ValidateScopes** so any missing constructor dependency now fails at container-build time with a precise descriptor (this exact error shape is pinned by a regression test). `ModeViewModel` and `HomeViewModel` received constructor-injected dependencies instead of service-locator calls. `MainWindow` no longer hard-casts `IToastService` to its concrete class (new `IToastPresenter` capability interface — caught by the headless tests).

## Verification matrix

| Area | Status | How |
|---|---|---|
| Startup crash regression | **Passed** | `DiAuditTests.Regression_*` (resolve ModeViewModel + all 12 VMs from production-shaped container) |
| Full DI audit | **Passed** | 10 audit tests: every domain/integration/UI service resolves; VMs are singletons; proxy wired to settings; ValidateOnBuild catches missing deps at build time |
| All 10 UI pages | **Passed** (headless) | `HeadlessUiTests`: real MainWindow + all pages construct via compiled XAML under Avalonia headless (Skia-rendered); all 10 page DataContexts injected; navigation to every page; Show/Close lifecycle; Mode cards populate |
| Startup/shutdown lifecycle | **Passed** (headless) | same suite — window Show→RunJobs→Close on the real app |
| GUI rendering on a real display | **Not tested** | sandbox has no display server (honest limitation, KNOWN_ISSUES.md) |
| Release build linux-x64 | **Passed** | `packaging/publish.sh` (Release config, tests first) → 99 MB self-contained bundle |
| Native smoke test | **Passed** | binary executes; reaches `XOpenDisplay` (no display in sandbox — expected headless result) |
| install.sh / uninstall.sh | **Passed** | executed with `DEST_OVERRIDE` (user-local, no sudo): files land correctly, desktop entry Exec rewritten for the actual destination (bug found+fixed), icons staged, uninstall removes app files but **preserves user data** (`~/.config/LuaToolsNova`) |
| System (/opt) install | **Not tested** | requires root; script logic identical modulo destination |
| Live Steam/network flows | **Not tested** | no Steam/network services in sandbox; endpoints identical to original; logic unit-tested |
| Linux Mint 22.3 (Ubuntu 24.04 base) | **Partially verified** | Debian 13 sandbox shares the XDG/systemd/dotnet stack; XDG paths, symlink handling, permissions and env stripping are unit-tested; a real Mint desktop run is still open |

## Test totals

**116 tests, 0 failures**: Core 56 · Linux 16 · Application 26 · Integration 18.

## Next actions

1. Install the v1.0.1 release bundle on a real Linux Mint 22.3 desktop and confirm window rendering + screenshot comparison (original design tokens preserved).
2. Exercise live Steam/network flows; record results in TEST_RESULTS.md.
3. Configure the Velopack Linux channel; promote the pre-release to stable once desktop-verified.
