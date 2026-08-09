# Warudo Resource List Cache

A single-file Warudo Playground plugin that removes the multi-second UI freeze when opening
asset **Source** dropdowns (Prop / Environment / Character / Particle) in libraries with many
mods or Steam Workshop subscriptions.

## Problem

Every dropdown open triggers a full rescan, synchronously on the main thread:

- Every `.warudo` file is opened to parse its mod header (and every `.vrm` its meta)
  just to obtain the display name
- Each provider is scanned **twice** per query due to a double invocation in
  `ResourceManager.ProvideResources`
- The result cache lasts only 1 second, so this happens on virtually every open

With 348 Workshop subscriptions (817 `.warudo` files) plus ~300 local mods, each dropdown
open froze the UI for 2-3 seconds.

## How it works

At runtime the plugin swaps the 8 stock resource providers (local + Workshop, for the 4
asset queries above) with caching versions, via reflection. Game files are not modified.

- Display names are cached persistently, keyed by `(path, mtime)` — a file is parsed only
  when it is new or has changed
- Directory enumeration stays live, so newly installed mods always appear
- A 3-second memo absorbs the double-invocation cost
- Any failure falls back to the stock providers. If a Warudo update breaks the reflection
  targets, nothing is swapped and stock behavior returns (a warning is logged)

## Results (measured)

| Query | Before | After (warm) |
|---|---|---|
| Prop, 189 local files | seconds | ~19 ms |
| Prop, 479 Workshop entries | seconds | ~110 ms (pure enumeration) |

Output lists are identical to stock — verified by diffing against the original providers
on first run.

## Install

Copy `src/ResourceListCachePlugin.cs` into `Warudo_Data/StreamingAssets/Playground/`.
Warudo compiles and loads Playground scripts automatically (restart Warudo after copying).

- Logs are tagged `[ResourceListCache]` in `Player.log`
- Cache file: `<persistentDataPath>/moraecat_resource_name_cache.json`
  (`AppData/LocalLow/HakuyaLabs/Warudo/` on Windows) — safe to delete anytime; it rebuilds
- To remove the plugin, delete the file from Playground and restart

## Caveats

- Relies on Warudo runtime internals via reflection, so a game update may stop it from
  working — it fails safe (no swap, stock behavior, warning in log)
- Playground-only by design: uMod plugin mods disallow `System.Reflection`, so this cannot
  be packaged as a `.warudo` mod
- Playground scripts run without sandboxing. It is a single file — read it before installing

## 한국어

모드/워크숍 구독이 많을 때 Prop·Environment 등 에셋 소스 드롭다운이 수 초씩 멈추는 문제를
해결하는 Playground 플러그인입니다. `src/ResourceListCachePlugin.cs` 파일 하나를
`Warudo_Data/StreamingAssets/Playground/` 폴더에 복사하고 Warudo를 재시작하면 됩니다.
제거는 파일 삭제 후 재시작.

---

Author: [moraecat](https://github.com/moraecat) · License: MIT
