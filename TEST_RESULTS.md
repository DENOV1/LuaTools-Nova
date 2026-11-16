# TEST_RESULTS — LuaTools Nova v1.0.1

Environment: Debian GNU/Linux 13 (trixie), .NET SDK 8.0.428, xUnit 2.9.2, Avalonia 11.2.8 + FluentAvaloniaUI 2.3.0. Sandbox has **no display server, no Steam, no live lua.tools/Hubcap services**. Every result below labels how it was verified. Nothing mocked is presented as real-world.

## 0. v1.0.2 (Mint test round 1 fixes)

```
$ dotnet test LuaTools.Nova.sln --nologo
Passed! 56/56 Core · 16/16 Linux · 26/26 Application · 25/25 Integration   (123 total)
```
New regression tests (8) cover: Fetch CanExecuteChanged firing, Fetch disable-while-checking,
DetailsHeaderUrl per-game association, Fixes null-response error state, Fixes valid-response
populate + error clear, Steam-missing honest banner, Mode card ShowActionButton INPC.
CI (ubuntu-latest): green on the fix commit.

## 1. Build (Release, clean outputs)

```
$ rm -rf artifacts bin obj src/*/bin src/*/obj tests/*/bin tests/*/obj
$ ./packaging/publish.sh        # runs dotnet test -c Release FIRST, then publish
Passed! 56/56 Core · 16/16 Linux · 26/26 Application · 18/18 Integration
Build succeeded. 0 Errors
99M  artifacts/luatools-nova-linux-x64/
```
**Passed.**

## 2. Automated tests (Debug and Release configurations, both green)

```
$ dotnet test LuaTools.Nova.sln --nologo
Passed! 56/56  Core.Tests
Passed! 16/16  Linux.Tests
Passed! 26/26  Application.Tests   (+10 new DI-audit tests)
Passed! 18/18  Integration.Tests   (+5 new headless UI tests)
TOTAL: 116 tests, 0 failures
```
**Passed.**

### What the tests actually verify (unit / mocked-integration labeled)

| Suite | Tests | Type |
|---|---|---|
| Lua grammar parse/comment-toggles/key-merge/diff | 6 | Unit |
| LuaVault content-addressed store (capture/apply/dedupe/delete-guard/update-in-place) | 5 | Unit (temp-dir filesystem) |
| Manifest binary codec (raw+zipped, identity match, garbage rejection) | 4 | Unit |
| Settings atomic write + corrupt→.bak recovery; CacheService caps/dedupe | 6 | Unit (temp-dir filesystem) |
| Steam path discovery: bootstrap symlink (resolved), debian fallback, Flatpak flag, registry.vdf language, override precedence | 5 | Unit (fixture $HOME) |
| Library roots union (config/+steamapps/ vdf) + missing-installdir skip | 2 | Unit (fixtures) |
| Proton compat (native runtimes ≠ proton) + WINEDLLOVERRIDES merge/idempotence/removal | 8 | Unit |
| Download queue dedupe/cancel/history/pause-resume/-validate/retry-progress; rate window + ETA | 11 | Unit (inline dispatcher) |
| Pager windowing + "All" mode | 3 | Unit |
| Link parser; mode migration; DoH parser; byte format | 11 | Unit |
| **DI audit (NEW)**: every registered service resolves; all 12 ViewModels resolve; singletons; proxy↔settings wiring; ValidateOnBuild catches missing deps | 10 | Integration (real container, boundary fakes) |
| **Headless UI (NEW)**: real MainWindow via compiled XAML; all 10 page DataContexts injected; navigation to all 9 pages; Show/Close lifecycle; Mode cards | 5 | Integration (real Avalonia app, headless Skia rendering — **not** pixel-verified) |
| Fix-record codec + path containment + SafeFixKey | 4 | Unit |
| ToolPaths XDG layout; plugin popup DTO camelCase contract; HTTP status mapping | 8 | Unit |
| Steam integration with **real Steam** | 0 | **Not tested** (no Steam in sandbox) |
| Live lua.tools/Hubcap/Supabase/DepotDownloader calls | 0 | **Not tested** (endpoints identical to original; logic unit-tested) |
| GUI rendering on a real display | 0 | **Not tested** (no display) |

## 3. Bugs discovered & fixed this audit (each with a regression test)

1. **[CRITICAL — the reported crash]** Split composition root → `UnlockerServiceProxy` (and 14 more) unregistered. Fixed: single container + ValidateOnBuild. Tests: `Regression_*`, `Audit_*`.
2. **[MAJOR]** `MainWindow` hard-cast `IToastService` → `ToastServiceImpl` (would throw for any alternate implementation). Fixed with `IToastPresenter` capability interface. Caught by headless tests.
3. **[MINOR — packaging]** `install.sh` desktop entry kept `Exec=/opt/...` even for user-local installs; `mkdir -p` missing for user-local applications dir. Fixed; verified by executing the scripts.
4. **[MINOR — tooling]** `publish.sh` failed when `dotnet` wasn't on PATH. Fixed (workspace SDK autodetected).

## 4. Release build + smoke test

```
$ ./packaging/publish.sh
-rwxr-xr-x luatools-nova (ELF 64-bit LSB pie executable, x86-64)
99M artifacts/luatools-nova-linux-x64/

$ ./artifacts/luatools-nova-linux-x64/luatools-nova
System.Exception: XOpenDisplay failed
   at Avalonia.X11.AvaloniaX11Platform.Initialize(...)
```
**Passed** — bundle executes natively; the only failure is "no display server in sandbox", the expected headless endpoint. `luatools-nova.ico/.png/.desktop` staged beside the binary.

## 5. Packaging scripts (actually executed)

```
$ DEST_OVERRIDE=… ./packaging/install.sh      → binary + .desktop + hicolor icons installed;
                                                desktop Exec rewritten to the real destination
$ DEST_OVERRIDE=… ./packaging/uninstall.sh    → app files removed; ~/.config/LuaToolsNova preserved
```
**Passed** (user-local mode, no sudo). System `/opt` mode: **Not tested** (needs root; same code path).

## 6. GUI on real display / Linux Mint 22.3 desktop

**Not tested.** No display server here. Marked as the headline limitation of the v1.0.1 release (pre-release).
