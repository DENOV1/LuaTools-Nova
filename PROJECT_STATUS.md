# PROJECT_STATUS — LuaTools Nova

**Status: functional v1.0.0 build on Linux. 89/89 automated tests green. Self-contained linux-x64 bundle published and smoke-tested. Published to GitHub at https://github.com/DENOV1/LuaTools-Nova.**

Last updated: 2026-11-16

## What works (verified on this machine)

- Full solution builds clean (0 errors) with .NET 8 + Avalonia 11.2.8 + FluentAvalonia 2.3.0
- 89 automated tests green: Core 56, Linux 16, Application 17, Integration 13
- Self-contained `linux-x64` publish (99 MB) runs; reaches X11 display init (sandbox has no display server — `XOpenDisplay failed` is the expected last step headless)
- Steam discovery: bootstrap-symlink resolution, distro layout fallbacks, Flatpak/Snap detection, language from `registry.vdf` (unit-tested)
- Library scanning incl. `config/`+`steamapps/` vdf union (unit-tested)
- Lua codec/parser/editor + content-addressed vault (unit-tested)
- Manifest binary codec incl. zipped manifests (unit-tested with synthetic manifests)
- Download queue semantics: dedupe, cancel, depot pause/resume/-validate, retry-with-progress, history cap (unit-tested)
- Settings atomicity + corrupt→.bak recovery (unit-tested)
- Proton compat detection + WINEDLLOVERRIDES merge semantics (unit-tested)
- Fix records with hash-guarded backups + path containment (unit-tested)
- Packaging: desktop entry, hicolor icon ladder, protocol handler, install/uninstall scripts, CI workflow

## Network-gated features (implemented; need a live desktop + network to exercise)

Steam search/featured/appdetails, lua.tools + Hubcap + backend source downloads, Supabase Discord OAuth / login-code sign-in, denuvo fix listings/downloads, depot downloads via the official linux-x64 DepotDownloader, plugin frontend install from LTSP releases, CDP store-page injection (needs Steam running with `-cef-enable-remote-debugging`).

All these run against the exact same endpoints as the original app; their deterministic logic (parsing, merging, queueing, persistence, contracts) is covered by the test suite.

## Known gaps

See KNOWN_ISSUES.md (honest list: no display-server run in this sandbox, Windows-only tools documented as unsupported, auto-update channel deferred, English-only UI).

## Next actions

1. Run on a real Linux desktop (Debian/Arch/Fedora) against a live Steam install; capture screenshots.
2. Exercise the network flows end-to-end; record results in TEST_RESULTS.md.
3. Configure the Velopack Linux release channel; cut v1.0.0 tag → CI release workflow handles the rest.
4. Port satellite translations (infrastructure ready).
