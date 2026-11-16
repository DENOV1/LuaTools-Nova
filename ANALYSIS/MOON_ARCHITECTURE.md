# luatools-moon — Architecture Analysis (Linux technical reference)

Source: https://github.com/swwayps/luatools-moon (AGPL-3.0). The Lua backend modules, shell workers, installer, and test suite were analyzed. **No Moon code was copied** — knowledge only (see LICENSING_ANALYSIS.md).

## Identity

luatools-moon is **not a desktop app**. It is a **Steam client plugin** (Millennium/lumen-style) plus an installer that bootstraps a Linux injection stack:

```
slsteam-moon  (LD_AUDIT injector; wrapper at ~/.local/share/SLSsteam/path/steam)
   └─ Lumen (~/.local/share/Lumen/lumen) sidecar
        └─ plugin at ~/.local/share/Lumen/luatools
             ├─ backend/*.lua   (RPC surface)
             └─ public/luatools.js (frontend injected into Steam's UI)
```

## Key Linux-specific knowledge extracted

1. **Steam discovery**: `~/.steam/steam` must be a *symlink* resolving to a dir with `steam.sh` (targets: `~/.local/share/Steam`, `~/.steam/debian-installation`). A real directory there blocks Valve's bootstrap. Flatpak `com.valvesoftware.Steam` and Snap paths detected separately; native install required.
2. **Steam-runtime env hazard**: spawned GUI binaries crash under Steam's 32-bit `LD_LIBRARY_PATH/LD_AUDIT`. All spawning strips `LD_LIBRARY_PATH LD_PRELOAD LD_AUDIT STEAM_RUNTIME_LIBRARY_PATH STEAM_ZENITY` and uses `setsid`.
3. **Clean shutdown ladder**: `steam -shutdown` first (clean bootstrap `Shutdown` line avoids next-boot re-verification) → SIGTERM → SIGKILL; only `steam`/`steamwebhelper` gate a relaunch. SteamOS restarts `steam-launcher.service`; Bazzite restarts `gamescope-session*` (discovered generically, never hardcoded to "-plus").
4. **Libraries**: union of `config/libraryfolders.vdf` AND `steamapps/libraryfolders.vdf` (drift between them historically made games report "not installed").
5. **Install state**: appmanifest `StateFlags==4` + bytes complete + dir exists.
6. **Proton detection**: `config/config.vdf` → `CompatToolMapping.<appid>.name`; `steamlinuxruntime*` ≠ Proton.
7. **WINEDLLOVERRIDES**: for fix DLLs colliding with Wine builtins (winmm, d3d*, xinput*, …), `x=n,b`; override must PRECEDE `%command%` (only leading `VAR=` tokens are read as env).
8. **Launch options**: read from `userdata/<id>/config/localconfig.vdf` across all Steam roots.
9. **Client language**: `~/.steam/registry.vdf` `"language" "brazilian"` → locale map.
10. **Parallel source racing + validated merge** (smart_download.sh + smart_merge.lua): NUL-separated candidate records, per-source zip-slip/symlink/size validation, coverage closure over expected `<depot>_<gid>.manifest` pairs, crash-safe journal publish into `~/.config/SLSsteam/manifests` + `stplug-in`.
11. **Fix apply/rollback**: staged apply with journal + per-file backups; `.slssteam_fix_dlls` / `.slssteam_fix_launchers` manifests; launcher-redirect launch option `bash -c "cmd=(%command%)"'; cmd[-1]="$PWD/Launcher.exe"; "${cmd[@]}"`.
12. **PII-scrubbed diagnostics**; immutable-distro rules (Bazzite/SteamOS: never invoke package managers, never sudo).

## Test suite

~65 tests (bash/luajit/node/python) pinning installer behavior, distro detection fixtures, no-sudo on immutable, downloader manifest/DLL listing, zip-slip + symlink attacks, bearer-token hygiene, launch-option merge/strip, proton compat gating, atomic JSON.

## Design conclusions for LuaTools Nova

1. The valuable knowledge is behavioral (above), all reimplemented as original C#.
2. Moon is a plugin *inside* Steam; Nova is a desktop app that reproduces the same behaviors with its own UI, and talks to Steam's CDP (port 8080) for store-page injection when available — no slsteam/Lumen dependency.
3. **No code reuse**: Moon is AGPL-3.0; Nova's Linux modules are independent C# implementations of documented behavior.
