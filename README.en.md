# Warudo Resource List Cache

[한국어](README.md) · **English** · [日本語](README.ja.md)

**Download: [ResourceListCachePlugin.cs](https://github.com/moraecat/warudo-resource-cache/releases/latest/download/ResourceListCachePlugin.cs)**

---

A Warudo Playground plugin that fixes multi-second freezes of the asset **Source dropdowns** (Prop / Environment / Character / Particle) in libraries with many mods or Workshop subscriptions.

## Cause and fix

- Cause: every dropdown open re-parses every mod file just for display names (doubled by a double-invocation bug, with only a 1-second cache)
- Fix: swaps the 8 stock providers with caching versions via reflection. Names are cached persistently by (path, mtime), so only new or changed files are parsed. Game files are not modified
- Measured (300 local mods + 348 Workshop subscriptions): 189 local props from seconds to **~19 ms**, 479 Workshop entries to **~110 ms**, lists identical to stock

## Thumbnail crash and freeze protection

- Cause: picker thumbnails parse `.warudo` files on a web-server thread; when that overlaps a mod load on the main thread, Warudo crashes (reproduced by picking a prop and immediately throwing/adding an asset)
- Fix: thumbnail parsing is moved onto the main thread so the two can never overlap. Since 0.3.0 it handles **one per frame** so opening a picker no longer freezes the screen, and finished thumbnails are **cached on disk** by (path, mtime, size) so they are never parsed again. Concurrent requests for one file share a single parse; the memory cache is capped at 32 MB
- Thumbnail cache: `AppData/LocalLow/HakuyaLabs/Warudo/moraecat_thumbnail_cache/` (entries unused for 60 days or beyond 300 MB are pruned at startup; safe to delete)

## Install

1. Get `ResourceListCachePlugin.cs` from the Download link above and copy it into `Warudo_Data/StreamingAssets/Playground/`
2. Restart Warudo (remove: delete the file and restart)

Logs are tagged `[ResourceListCache]` in Player.log. Cache file: `AppData/LocalLow/HakuyaLabs/Warudo/moraecat_resource_name_cache.json` (safe to delete; it rebuilds).

## Caveats

- Relies on runtime internals via reflection — if a Warudo update breaks it, nothing is swapped and stock behavior returns (fail-safe)
- Playground-only: uMod plugin mods disallow `System.Reflection`, so it cannot ship as a `.warudo` mod
- Playground scripts are unsandboxed; it is a single file — read it before installing

---

Author: [moraecat](https://github.com/moraecat) · License: MIT
