<p align="center">
  <img width="192" alt="LuaTools Nova" src="assets/icon/luatools-nova_256.png" />
</p>

# LuaTools Nova

**LuaTools Nova is a Linux-native desktop application inspired by the original LuaTools, rebuilt with C# and Avalonia UI to provide a complete Steam management experience designed specifically for Linux.**

LuaTools Nova browses and installs Steam manifest/lua sources, edits `stplug-in` lua files (depot pinning, per-depot enable/disable), switches between stored build variants, downloads depot content, applies game fixes, and injects a companion plugin into Steam's store pages — all natively on Linux.

> ⚠️ **Fair-use notice:** LuaTools Nova is a management tool. You are responsible for complying with Steam's terms and the law in your jurisdiction.

## Requirements

- Linux x86_64 (tested on Debian 13 "trixie"; any distro with a working .NET 8 runtime or the self-contained build)
- **Native Steam** installed via your package manager (Flatpak/Snap Steam are detected but only partially supported)
- Optional: `xdg-open`, `unzip` (fix archives), `update-desktop-database` (protocol registration)

## Installation

### From source

```bash
git clone https://github.com/DENOV1/LuaTools-Nova.git
cd LuaTools-Nova
./packaging/publish.sh          # → artifacts/luatools-nova-linux-x64/
./artifacts/luatools-nova-linux-x64/luatools-nova
```

### System install (desktop entry + icon + protocol handler)

```bash
sudo ./packaging/install.sh     # installs to /opt/luatools-nova + .desktop + icons
# remove with: sudo ./packaging/uninstall.sh
```

## Feature overview

| Page | What it does |
|---|---|
| **Home** | Library stats, plugin status, recent games, drag-and-drop installer |
| **Add** | Steam search, featured strips, manifest sources (lua.tools/Hubcap/Ryuu/Sushi), FastFetch, DLC unlocks |
| **Manage** | Your lua library as a filterable, sortable grid; detail flyout; multi-select delete |
| **Depots** | Per-game lua variants (build pinning), depot/DLC breakdown, per-depot enable/lock toggles, built-in editor, depot download |
| **Mode** | Unlocker backend selection (honest Linux reality: Custom; Windows DLL modes documented as unsupported) |
| **Fixes** | Denuvo/online fix listings with manifest/fix/revert flows (hash-guarded backups in `.luatools-fix/`) |
| **Plugin** | Installs the LuaTools store-page plugin; injects via Steam's CEF debug bridge |
| **Downloads** | Unified queue with pause/resume/retry, history |
| **Settings** | Steam path, install behavior, autostart, DNS mode (Cloudflare DoH fallback), Hubcap key |

## Architecture

```
src/
├── LuaTools.Core/           models, lua codec, vault, manifest codec, persistence, queue model
├── LuaTools.Infrastructure/ network stack, API clients, download queue, auth, depot tool runner
├── LuaTools.Linux/          Steam discovery, process control, desktop integration, Proton/Wine logic
├── LuaTools.Application/    composition root + ViewModels
├── LuaTools.Integrations/   Steam CEF bridge, local HTTP backend, plugin installer
└── LuaTools.UI/             Avalonia app (NavigationView shell + 10 views)
tests/                       Core.Tests, Linux.Tests, Application.Tests, Integration.Tests
ANALYSIS/                    full reverse-engineering documentation
```

Key Linux behaviors: Steam path discovery honors the `~/.steam/steam` bootstrap symlink (validated, never created); libraries merge `config/` and `steamapps/` `libraryfolders.vdf`; spawned processes strip Steam-runtime env vars; Steam restart uses a clean `steam -shutdown` ladder with gamescope/SteamOS systemd awareness; no root required anywhere.

## Building

```bash
dotnet build LuaTools.Nova.sln
dotnet test  LuaTools.Nova.sln     # 89 tests
dotnet publish src/LuaTools.UI -c Release -r linux-x64 --self-contained -o artifacts/luatools-nova-linux-x64
```

## Documentation

- [ANALYSIS/](ANALYSIS/) — reverse-engineering docs: original architecture, Moon architecture, dependency map, Windows→Linux implementation map, feature compatibility matrix, UI design tokens, licensing analysis.
- [PROJECT_STATUS.md](PROJECT_STATUS.md), [FEATURE_TRACKER.md](FEATURE_TRACKER.md), [TEST_RESULTS.md](TEST_RESULTS.md), [KNOWN_ISSUES.md](KNOWN_ISSUES.md)

## Credits

- [LuaTools](https://github.com/madoiscool/LuaTools) (MIT) — the original Windows application; the functionality and design source of truth.
- [luatools-moon](https://github.com/swwayps/luatools-moon) (AGPL-3.0) — Linux behavioral reference (knowledge only; no code reused).
- [Avalonia](https://avaloniaui.net/) + [FluentAvalonia](https://github.com/amwx/FluentAvalonia) — UI framework.
- [SteamRE / DepotDownloader](https://github.com/SteamRE/DepotDownloader) — depot content (fetched at runtime).

## Licence

MIT — see [LICENSE](LICENSE) (includes the upstream MIT notice) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
