# Frame-spike plan

Branch `perf-spikes`, from `main` at 60aab42. Started 2026-11-02.

**Symptom:** frame-time spikes on the top screen when a lot animates at once.
The usual case is combat card draws and discards, which the lower screen mirrors
with card motion, count rolls and badge bumps.

This plan comes from three research passes:
- the lower-screen frame pipeline;
- the lower-screen C# work done each frame and on each event;
- the engine and runtime.

Line numbers refer to 60aab42.

## Why it spikes

Several costs land in the same frames as the top screen's heaviest work.

1. **The lower screen rebuilds the pile 3–5 times per card burst.**
   - `PileContentsChangedPrefix` (`DsGameHooks.cs:1467`) starts a 0.12 s
     timer on the *first* change only. A discard spread over ~0.6 s triggers
     several full rebuilds.
   - Each rebuild does the following:
     - calls the game's whole-grid rebuild through uncached reflection;
     - takes two snapshots, each doing reflection and `GetGlobalRect` per holder;
     - may call `AllocateCardHolders` up to 64 times;
     - creates a new tween with about 2 `TweenProperty` calls per visible card
       (`RiseIn`, `:1749`).
   - Each rebuild also fast-forwards the previous tween (`SettleCards`). That
     work is thrown away and the cards visibly pop.
2. **Card motion lifts the 15 FPS combat cap.**
   - Every `DsStyle.Animate` extends `_pageMotionUntilUsec`
     (`DsBottomScreen.cs:415`).
   - That raises the page to the full configured FPS (`:683`).
   - Every changed capture calls `MarkChanged()` again (`DsCapture.cs:229`),
     which keeps the page at full rate for another second.
3. **Each changed capture costs about three full-frame CPU copies on the game thread.**
   - The readback is async (`TextureGetDataAsync`). Its callback, however, runs
     inside `swap_buffers` on the main thread.
   - The callback copies the staging buffer into a `PackedByteArray` (Godot),
     that into the shadow buffer (`sts2ds.c` submit), and the shadow into the
     window buffer (`sts2ds.c` present).
   - `ANativeWindow_lock` can wait up to one vsync for a free buffer.
   - Estimate: 2–4 ms per capture.
   - Up to 4 parts × `MaxInFlight` 3 can complete in the same frame.
   - The hash skip happens only after all of that work.
4. **Animations add to garbage-collection pressure.**
   - Each `TweenProperty(x, "modulate:a", …)` creates a new `NodePath` and a new
     `PropertyTweener`, both finalizable.
   - A rebuild creates about 80 of each. A count change creates 7–9 of each,
     plus a `Callable.From` closure (`RollCount`/`BumpBadge`, `DsDock.cs:421-487`).
   - `DsParticles.OnNodeAdded` (`DsParticles.cs:22`) runs for every node the
     game adds and creates a managed wrapper each time. It is on by default
     because `particle_amount` is 50.
5. **The engine compiles shaders and code on first use.**
   - **Shaders:** Godot 4.5 doesn't precompile 2D pipelines, so each new
     shader/blend/target combination compiles on first draw. The Vulkan
     pipeline cache saves only at 3 MB or on a clean exit. Android usually kills
     the app rather than closing it cleanly, so the cache may never be written.
   - **Warmup check:** `DsWarmupCache` skips the warmup when `shader_cache` is
     non-empty (`DsWarmupCache.cs:115,187`). It never checks
     `files/vulkan/pipelines*.cache`.
   - **Code:** the runtime is Mono, nothing is AOT-compiled, and there is no
     tiered JIT. Every new card, power or VFX code path is JIT-compiled on the
     main thread the first time it runs. The compat pack's combat code warmup
     is off (`GameSettingsFile.kt:88`).
   - **GC:** SGen runs with its defaults, which are probably a small nursery
     and the non-concurrent major collector. `GamePrep.kt:424` sets only
     HOME and TMP.
6. **Smaller causes.**
   - The dock's selected tab rocks forever through a `TweenMethod` lambda. That
     calls managed code every frame and keeps the strip captured at 30 FPS for
     the whole fight.
   - The map copy keeps processing while its page is hidden.
   - Reflection runs per frame in `InRun`/`MapAmbient` (`DsGameHooks.cs:317-326`).
   - Nothing asks the CPU scheduler to boost (no ADPF), so clock ramp-up lags
     a sudden burst.

## Ground rules

- Build only: `make apk` and `make test`. The user installs and runs device
  tests (see the memory note on not installing to the device).
- Keep `preload_combat_animation_warmup_mode` **off**. It crashes every fight.
  `preload_combat_code_enabled` is a different setting and is safe to try.
- One commit per numbered item, so each can be bisected and reverted alone.
- Compare every change against the baseline stats line (Phase 0).

## Phase 0: measure first

Without this, nothing below can be shown to help.

**0.1 Complete the 10 s stats line** (`DsBottomScreen.cs:868`).
- Add deltas of `Performance.Monitor.PipelineCompilationsCanvas`, `…Draw` and
  `…Specialization`.
- Add `captures_cb_max`: the most capture callbacks in one frame.
- Add `cb_ms_max` and `present_ms_max`. These time *all* of `OnFrameData` and
  `OnPostDraw`/`Present`. Today `readback_avg` only times the request call and
  `submit_avg` only times `ds_submit_*`.
- Add `pile_rebuilds`.

**0.2 Hitch log.** On any frame over 20 ms, log one line with:
- the frame time;
- pipeline compiles in that frame;
- GC counts in that frame;
- the number of capture callbacks in that frame;
- whether a pile rebuild or tween started in that frame.

This alone should tell shader, JIT, GC and lower-screen spikes apart.

**0.3 Viewport render time.** Call `RenderingServer.ViewportSetMeasureRenderTime(rid, true)`
on the page and strip viewports. Log `ViewportGetMeasuredRenderTimeCpu/Gpu` in
the stats line.

**0.4 ATrace markers.**
- In `sts2ds.c`, add `ATrace_beginSection`/`endSection` around submit, lock,
  copy and post.
- Export `ds_trace_begin(const char*)`/`ds_trace_end()` so C# can mark `Tick`,
  `OnFrameData`, `Present` and `RefreshPile`.
- The manifest is already profileable, so the user can record a Perfetto trace
  with the `gfx`, `view`, `sched` and `freq` categories plus the app's atrace.

**Baseline protocol (user, on device).** Run each step on the same fight
(the first fight of a seeded run is fine):
1. Lower screen at the default setting.
2. Lower screen at `fps: 10`.
3. Lower screen at `enabled: false` (both set in `dualscreen.json`).

Record `hitches`, `frame_max`, `gc_minor`/`gc_major` and the new counters for
each. The difference between step 1 and step 3 is the lower screen's share of
the spikes. The rest comes from the engine and runtime.

## Phase 1: quick wins (low risk, high payoff)

**1.1 Coalesce pile rebuilds.** File: `DsGameHooks.cs:1462-1530`.
- Replace the first-change timer with a trailing debounce. Each change pushes
  the due time out to `now + 0.12 s`, capped at 0.35 s after the first change
  in the burst.
- Drive it from `ProcessFrame`: keep a due time per stale pile and check it
  each frame. That replaces `CreateTimer` plus a closure per burst.
- Don't start a rebuild while that pile's card tween is still running. Push
  the rebuild back to when the tween ends.
- Rebuild at most one pile per frame.
- Check: `pile_rebuilds` per discard burst goes from 3–5 to 1, and cards no
  longer pop.

**1.2 Keep the combat cap during card motion.** File: `DsBottomScreen.cs:415, 683`.
- In combat, card motion may raise the page only to `CombatFps` (15), or 20 if
  15 looks choppy. Outside combat it keeps the full rate.
- While `_budget > 0` (the game is already hitching), use the budget's lower
  rate even during motion.
- In `DsCapture.cs:229`, give the page the shorter, explicit `seconds` instead
  of the default 1 s re-arm. Stop renewing the active window once a part has
  changed for N frames in a row.
- Check: in combat, `captures` per 10 s drops sharply, and the hitch log no
  longer shows capture callbacks in spike frames.

**1.3 Turn on combat code warmup.** File: `GameSettingsFile.kt:88`.
- Set `preload_combat_code_enabled` to `true`. The compat pack then runs
  `PrepareMethod`/`RunClassConstructor` over combat code during loading.
- Device test: one full fight, plus starting a second fight. Confirm there's no
  crash (unlike the animation warmup).
- Check: fewer first-fight hitches. The hitch log should show no compiles or GC
  in the frames that used to spike.

**1.4 GC settings.** File: `GamePrep.kt:420-432`.
- Set the following before the runtime loads:
  `Os.setenv("MONO_GC_PARAMS", "nursery-size=32m,major=marksweep-conc", true)`.
- Make it a launcher setting (off, 16m, 32m), so it can be A/B tested without
  rebuilding.
- If the nursery setting looks ignored, a temporary `MONO_LOG_LEVEL=debug
  MONO_LOG_MASK=gc` shows the pause times in logcat.
- Check: `gc_minor` per 10 s drops and memory stays reasonable
  (`dumpsys meminfo dev.sts2ds`).

**1.5 Cache fixed values and reflection.** Files: `DsGameHooks.cs`, `DsDock.cs`.
- Add `static readonly NodePath` fields for `"position:y"`, `"modulate:a"`,
  `"position"`, `"scale"` and `"modulate"`, and use them in every `TweenProperty`.
- Resolve the following once with `AccessTools.FieldRefAccess`,
  `MethodDelegate` or cached `PropertyInfo` getters turned into delegates:
  - `PileButtonTab`, `UpdatePileCount`, `DeckCountPostfix`, `AnimateExits`,
    `PileGrid`: `_pile`, `_currentCount`, `Type`, `Cards`, `_grid`;
  - `OnPileContentsChanged`, `AllocateCardHolders`;
  - `InRun`, `MapAmbient`.
- `TabForPile`: map PileType by its integer value, not `ToString()`.
- Check: `gc_minor` drops further. No behaviour change.

## Phase 2: medium effort

**2.1 One tween per pile change.** Drive all arriving and leaving cards from a
single `TweenMethod` over precomputed from/to arrays for position and alpha,
using a cached delegate rather than a new lambda each time. This replaces the
2×N `PropertyTweener` objects. File: `DsGameHooks.cs` (`AfterRebuild`, `RiseIn`,
`AnimateExits`).

**2.2 Coalesce dock badges.**
- Mark the count dirty and apply it once per frame, so a 5-card burst plays one
  roll and one bump instead of five restarted ones.
- Reuse the roll labels instead of creating new ones, and drop the
  `Callable.From` closure.
- File: `DsDock.cs:415-490`.

**2.3 Calm idle loops in combat.**
- While `InCombat`, stop the selected tab's rock and breath loops, or play them
  once when the tab is selected.
- At a minimum, replace the rock `TweenMethod` lambda with two looping
  `TweenProperty(rotation)` steps, so no managed code runs each frame.
- Check: the strip's capture rate falls to ambient in combat.

**2.4 Pause hidden pages.**
- Set `ProcessMode = Disabled` on pages that aren't active, including the map
  copy while a pile tab is showing.
- Re-check that `SetMap`, travel-state mirroring and opening the map from a
  travel tap still work, because they may depend on the map copy processing.

**2.5 Stagger captures.**
- At most one capture request per frame, round-robin over page, strip and
  corners.
- If more than one callback lands in a frame, queue the extra submits for the
  next frame. They're cheap to delay; they're already 2 frames old.

**2.6 Narrow particle thinning.**
- Replace the global `SceneTree.NodeAdded` hook (`DsParticles.cs:22`). Either
  patch the game's VFX spawn points, or thin once per new scene root with
  `FindChildren("*", "GPUParticles2D")` / `CPUParticles2D`.
- Look up the spawn sites in a decompile of the current game build (the old
  decompile in scratch is gone; see the worktree memory note for how to redo
  it).

**2.7 Stronger warmup cache check.** File: `DsWarmupCache.cs:105-120,185-190`.
- Count it as a MISS unless `files/vulkan/pipelines*.cache` exists, is
  non-empty and is newer than the game build.
- Check whether the pipeline cache file appears at all after a session the
  user ends by swiping the app away:
  `adb shell run-as dev.sts2ds ls -l files/vulkan files/shader_cache`.
- If it never appears, see 3.4.

## Phase 3: larger changes

**3.1 Native present thread.**
- The capture callback does one memcpy into a native ring slot, then returns.
- A pthread in `sts2ds.c` does the hash, compose, `ANativeWindow_lock`, copy
  and `unlockAndPost`.
- This takes one or two full-frame copies and the blocking lock off the game
  thread.
- The risk is in locking around the ring and around surface teardown
  (`ds_set_surface`).

**3.2 Dirty row bands.** Hash per band of rows, then lock and copy only the
changed bands (`ANativeWindow_lock` with a dirty rect). Pile and map motion is
usually local. This can be done before or with 3.1.

**3.3 ADPF performance hint.**
- On API 33+, `APerformanceHint_createSession` for the Godot main thread,
  targeting 16.67 ms.
- Report actual work time from `ProcessFrame` each frame.
- This helps the CPU clocks ramp up when a burst starts. Quick check
  beforehand: does the AYN performance mode alone remove the spikes?

**3.4 Make the pipeline cache persist.**
- Options:
  - trigger a clean save on `onStop`/`onPause`, if the engine exposes one;
  - patch `rendering/rendering_device/pipeline_cache/save_chunk_size_mb` lower
    in `project.binary` (only if the key is already present, and only with a
    patch that keeps the length the same);
  - otherwise rely on 2.7 so a real warmup always runs.
- Needs research into what Godot 4.5.1 exposes to C#.

**3.5 Pacing experiments.**
- A/B test `fps_limit=0` (vsync only) against 60. The Godot limiter stacked on
  vsync can miss a vsync.
- Check whether Swappy's auto mode drops to a 30 FPS swap interval after slow
  frames, and whether it conflicts with `RefreshRate.java` setting the
  preferred mode.
- Use `dumpsys SurfaceFlinger --latency`; `gfxinfo` doesn't see Godot's
  SurfaceView.

**3.6 Zero-copy lower screen.**
- Render into an `AHardwareBuffer`-backed `VkImage`
  (`VK_ANDROID_external_memory_android_hardware_buffer`, wrapped with
  `RenderingDevice.TextureCreateFromExtension`).
- Do a GPU `TextureCopy`, then post through `ASurfaceTransaction_setBuffer`.
- Removes all CPU copies. Highest payoff and highest risk (sync, driver).
- Consider only if Phases 1–2 plus 3.1 aren't enough.

**3.7 AOT `sts2.dll`.** Needs a Mono cross-compiler that matches this custom
runtime. Research only.

## Order and tracking

| # | Item | Status |
|---|---|---|
| 0.1–0.4 | Measurement (`DsPerf.cs`, hitch log, viewport render time, ATrace) | done: 0cef881, 66939d8, 9e90428, 63f4e10 |
| — | `files/dualscreen_overrides.json` (launcher rewrites `dualscreen.json` every launch) | done: 8c36697 |
| — | Baseline on device (user) | todo |
| 1.1 | Pile debounce | done: 135cac9 |
| 1.2 | Combat cap during motion (`combat_motion_fps`, 0.25 s re-arm) | done: 2366615 |
| 1.3 | Combat code warmup (launcher switch, default on) | done: efd14fb (needs device fight test) |
| 1.4 | GC nursery (launcher: 4/16/32 MB, default 32; major GC is already concurrent) | done: 8e60b1b |
| 1.5 | Cache NodePaths and reflection | done: 9d7c65a |
| 2.1 | One tween per pile change | done: 91ffb2d |
| 2.2 | Coalesce dock badges (per frame) | done: e9dfa89 |
| 2.3 | Calm idle loops in combat | done: eecbe7a |
| 2.4 | Pause hidden pages (`pause_hidden_pages`) | done: 81ef383 |
| 2.5 | Stagger captures (`stagger_captures`) | done: 13a6dab |
| 2.6 | Particle thinning via `AddChildSafely` postfix | done: b062d67, menu art eb3fe39 |
| 2.7 | Warmup cache needs a fresh Vulkan pipeline cache | done: dae5f36 |
| 3.1 | Native present thread (`present_thread`, default on) | done: ab53150 |
| 3.2 | Dirty row bands | done: d7c6708 |
| 3.3 | ADPF hint (launcher switch, default on) | done: 07cb9c7 |
| 3.4 | `override.cfg`: `save_chunk_size_mb=0.25` (no save API in 4.5.1) | done: 55fcfea |
| 3.5 | FPS limit and Swappy mode settings | done: 7885ba4 |
| 3.6 | Zero-copy lower screen | deferred until device numbers |
| 3.7 | AOT `sts2.dll` | not practical: custom Mono 9.0.7 build, AOT images must match it; per-update recompiles |

After each phase, the user re-runs the baseline protocol and the numbers go
here:

| Run | hitches / frames | frame_max | gc_minor | captures | notes |
|---|---|---|---|---|---|
| baseline | | | | | |
