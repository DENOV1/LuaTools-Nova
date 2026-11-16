# Linux Implementation Map (Nova's `LuaTools.Linux` layer)

Design rule: **no OS checks scattered in business code** — the application layer depends on interfaces (`ISteamLocator`, `IProcessControl`, `ISystemIntegrations`, `IDialogService`, `IToastService`, `INavigationHost`) and `LuaTools.Linux` provides the only Linux implementations. Every OS-touching service is unit-testable against fakes.

## Services

### SteamPathLocator (ISteamLocator)
1. Candidates in order: user override → `~/.steam/steam` (**must be a symlink** resolving to a directory containing `steam.sh`; the target is followed — `FileInfo.LinkTarget`/`DirectoryInfo.LinkTarget`, relative targets resolved against the link's directory) → `~/.local/share/Steam` → `~/.steam/debian-installation` → `~/.steam/root` → Flatpak (`~/.var/app/com.valvesoftware.Steam/data/Steam`) → Snap (`~/snap/steam/common/.steam/steam`).
2. Sandbox installs detected and reported (`DetectionSource` = "flatpak (limited support)" etc.) — native required.
3. `SteamLanguage` from `~/.steam/registry.vdf` (`"language"` value, lowercased).
4. `SetOverride` re-reads at runtime when the user changes the setting.

### SteamPaths
- `config/stplug-in` (lua dir), `<root>/depotcache` (manifests — sibling of steamapps), `appcache/appinfo.vdf`, `config/config.vdf` (CompatToolMapping, DecryptionKeys), both `steamapps/libraryfolders.vdf` AND `config/libraryfolders.vdf` (union), `userdata/*/config/localconfig.vdf`.

### ProcessControl (IProcessControl)
- `IsSteamRunning` — `pgrep -x steam` / `steamwebhelper`.
- `StopSteam` — `steam -shutdown` (only if running; `timeout -k 5 20` bound, 25s wait) → poll 12s → SIGTERM 3s → SIGKILL 2s. Never asks a non-running Steam to quit; never starts over a live client.
- `StartSteam` — resolved launcher, detached (`setsid nohup`), env stripped of the five Steam-runtime variables.
- `RestartSteam` — systemd-user fast path (`steam-launcher.service` on SteamOS, `gamescope-session*` on Bazzite/ChimeraOS, discovered generically) else stop+start.

### EnvGuard (inline in spawn helpers)
- Every external process Nova spawns from a Steam-inherited session (xdg-open, DepotDownloader, file managers) runs with `LD_LIBRARY_PATH LD_PRELOAD LD_AUDIT STEAM_RUNTIME_LIBRARY_PATH STEAM_ZENITY` unset — the verified Linux hazard where Steam's 32-bit runtime breaks spawned GUI binaries.

### ShellIntegrations (ISystemIntegrations)
- `OpenUrl` → `xdg-open` (single-quoted args, no shell interpolation of user data).
- `RevealPath` → `org.freedesktop.FileManager1` `ShowItems` if a file manager implements it, else open parent dir.
- `RegisterProtocol` → `$XDG_DATA_HOME/applications/luatools-nova.desktop` with `MimeType=x-scheme-handler/luatools;x-scheme-handler/luatoolsnova;` + `update-desktop-database` best-effort.
- `SetAutostart` → `~/.config/autostart/luatools-nova.desktop` (Exec with `--minimized`).
- `DownloadsFolder` → `xdg-user-dir DOWNLOAD` fallback `~/Downloads`.
- Pickers/clipboard/exit marshal to the UI host via a static bridge (the platform layer has no view).

### ProtonCompat + WineOverrides
- Forced compat tool per appid from `config/config.vdf` `CompatToolMapping` (name; `steamlinuxruntime*` counts as NOT Proton) — gates Windows-only fixes on native builds.
- `WINEDLLOVERRIDES` computation and idempotent launch-option merging (override first, single `%command%` preserved/self-healed).

### Filesystem rules honored everywhere
- XDG dirs; case-sensitive paths; symlink resolution bounded to expected roots; no root required anywhere; `chmod 600` on auth files; atomic writes (tmp→rename) with `.bak` twins as in the original.
