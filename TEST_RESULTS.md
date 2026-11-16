# TEST_RESULTS — LuaTools Nova

Environment: Debian GNU/Linux 13 (trixie), .NET SDK 8.0.428, xUnit 2.9.2, sandbox **without a display server and without Steam installed**. Commands and raw results recorded below; nothing is claimed beyond what was actually run.

## 1. Build

```
$ dotnet build LuaTools.Nova.sln -c Release --nologo -v q
Build succeeded.
    0 Warning(s)
    0 Error(s)
```
(One informational Avalonia warning AVLN3001 during publish — DI-injected window has no parameterless ctor; benign, same pattern as the original.)

## 2. Automated tests

```
$ dotnet test LuaTools.Nova.sln --nologo
Passed!  - Failed:     0, Passed:    56, Skipped:     0, Total:    56, Duration: 293 ms - Core.Tests.dll
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration:  95 ms - Linux.Tests.dll
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 186 ms - Integration.Tests.dll
Passed!  - Failed:     0, Passed:    17, Skipped:     0, Total:    17, Duration: 197 ms - Application.Tests.dll
TOTAL: 102 tests, 0 failed (per-suite totals above include parameterized cases)
```

Coverage highlights (what the tests actually pin):
- lua grammar parse/comment-toggles, key merge, diff
- manifest binary codec (raw + zipped), identity match, garbage rejection
- settings.json atomic write + corrupt→.bak recovery; cache caps/dedupe
- Steam path discovery (symlink resolution, debian fallback, flatpak flag, language, override)
- library roots union (config/ + steamapps/), missing-installdir skip
- Proton compat parsing (native runtimes ≠ proton) + WINEDLLOVERRIDES merge/idempotence/removal
- download queue dedupe/cancel/history/pause-resume/retry-with-progress; sliding rate window + ETA
- pager windowing + per-page "All"
- link parser (SteamDB/store/community/steam:// + depot/sub/bare-number rejection)
- mode migration (legacy → current), DoH json parsing + bypass rules, byte formatting
- plugin popup DTO camelCase JSON contract, fix record codec, path containment, ToolPaths layout

## 3. Tests that caught real product bugs (fixed)

1. `WineOverrides.BuildOverrides` — Lua-style lazy quantifier `.-` mistranslated to C# (matched 1-char DLL stems); fixed to `.+?`.
2. `SteamPathLocator` — did not follow the `~/.steam/steam` symlink; now resolves `LinkTarget` (relative targets supported).
3. `PluginAddStateDto` JSON contract — serialized PascalCase; the store-page plugin expects camelCase; now serialized with `JsonNamingPolicy.CamelCase`.

## 4. Publish + smoke test

```
$ ./packaging/publish.sh   # = dotnet test + publish -r linux-x64 --self-contained + staging
99M     artifacts/luatools-nova-linux-x64/

$ ./artifacts/luatools-nova-linux-x64/luatools-nova
Unhandled exception. System.Exception: XOpenDisplay failed
   at Avalonia.X11.AvaloniaX11Platform.Initialize(X11PlatformOptions options)
```
**Interpretation:** the self-contained bundle executes natively; managed + native components load; the run terminates only at X11 display init because this sandbox has **no display server**. This is the expected headless result and proves the bundle's runtime integrity, not GUI behavior.

## 5. Manual GUI verification — NOT YET PERFORMED (honest status)

The sandbox cannot run X11/Wayland, so the following are **not yet verified** and remain open items:
- Window render + navigation through all 10 pages
- Real Steam interaction (discovery against a live `~/.steam`, lua install into `config/stplug-in`, restart ladder)
- Live network flows (search/sources/auth/depots/fixes/plugin install/CDP injection)
- Desktop-entry launch, tray behavior, protocol handler from a browser

Record results here as they are run on a real desktop.

## 6. Distributions

| Distro | Status |
|---|---|
| Debian 13 (trixie) | **Build + tests + publish verified on this sandbox.** GUI run pending (no display). |
| Ubuntu 22.04/24.04, Fedora, Arch, SteamOS, Bazzite, Mint | **Not yet verified** — claimed as *targets*, not verified. CI (ubuntu-latest) covers build+tests on every push. |
