# PROJECT_STATUS — LuaTools Nova

**Status: v1.0.1 engineering-audit complete. Startup crash fixed at the architectural level. 116/116 automated tests green. Headless UI lifecycle verified (real MainWindow + all 10 pages + navigation). Self-contained linux-x64 release build published. GitHub Release v1.0.1 published as pre-release.**

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
