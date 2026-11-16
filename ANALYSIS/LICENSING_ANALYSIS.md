# Licensing Analysis

## Repository A — madoiscool/LuaTools
- **License: MIT** (LICENSE, "Copyright (c) 2026 LuaTools").
- Permits use, copying, modification, distribution, sublicense, sale — **with the condition that the copyright notice and permission notice accompany all copies or substantial portions**.
- Third-party runtime dependencies used by the original (WPF-UI MIT, CommunityToolkit.Mvvm MIT, Velopack MIT, Markdig, VirtualizingWrapPanel MIT, M.E.Hosting MIT) are permissive; Nova consumes them via NuGet normally.
- **Consequence for Nova:** porting/adapting original source is permitted. Nova carries the upstream MIT notice inside its own LICENSE block and `THIRD_PARTY_NOTICES.md`, and derived files keep the header comment `// Derived from madoiscool/LuaTools (MIT)`.

## Repository B — swwayps/luatools-moon
- **License: GNU Affero GPL v3** (LICENSE, full text).
- AGPL §13 + §5: copying/adapting the work obligates the derivative to be licensed AGPL and to offer source (including for network use). It also constrains derivative works of its code and its bundled `7zz` binary (7-Zip license, distinct).
- Factual content — file formats (VDF/acf, appinfo, manifest protobuf), Steam filesystem layout, protocol behavior, algorithmic ideas (parallel source racing, journal publication, clean shutdown ladder, WINEDLLOVERRIDES semantics) — is **not copyrightable subject matter**; reimplementing behavior in a different codebase, written independently, does not create a derivative work.
- **Nova policy (strict):**
  1. No Moon source file, snippet, or near-translation is included in Nova — verified by the inventory (Moon rows are INFO-ONLY/REIMPLEMENTED).
  2. Nova's Linux modules were written from the analysis's *descriptions* after the analysis pass, not alongside Moon code.
  3. Nova does **not** bundle Moon's `7zz` (7-Zip license + AGPL); it shells out to the user's own `unzip`/`7z` when available.
  4. Nova does not fork Moon's frontend `luatools.js`; the store-plugin feature fetches the upstream LTSP `plugin.zip` at runtime exactly as the original app does.
- **Consequence:** MIT is valid for Nova while rules 1–4 hold. This document is the audit trail.

## The attached icon
- Supplied by the requester as the intended Nova identity ("photo_2026-11-16_10-31-53.jpg"). Treated as the requester's own asset with instruction to use it; redistributed inside Nova's repo as `assets/icon/*` per the directive. No upstream LuaTools icon reused.

## Nova license
- MIT for all Nova code + docs, with the embedded upstream notice for the MIT-derived portions. `THIRD_PARTY_NOTICES.md` lists: madoiscool/LuaTools (MIT, derived portions), CommunityToolkit.Mvvm (MIT), Avalonia (MIT), FluentAvalonia (MIT), Markdig (MIT), Velopack (MIT), .NET (MIT). **AGPL components: none bundled.**
