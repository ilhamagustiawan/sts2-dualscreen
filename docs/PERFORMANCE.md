# Performance

The top screen should hold 60 FPS through a busy fight. This page lists what
costs frames, what the app does about each cost, the settings behind it and
how to measure.

## Where frames go

- **The lower screen.** Every frame shown down there is rendered by the game's
  engine, read back from the GPU and copied to the second display. Card
  draws and discards make the piles, the dock badges and the counters move
  together, in the same frames as the top screen's heaviest work.
- **Garbage collection.** Mono's minor collections pause the game thread, and
  tweens, node paths and closures made per card all add to them.
- **First-use compiles.** Godot 4.5 compiles each new shader pipeline the first
  time it is drawn, and Mono JIT-compiles every new code path on the main
  thread (the runtime is not AOT-compiled).
- **CPU clocks.** A sudden burst of work runs at idle clocks until the governor
  catches up.

## What is done about it

### Lower screen

- **Paced captures.** A part of the lower screen renders only when a capture
  is due: at the active rate while something on it moves, at an ambient rate
  for endless loops, otherwise once a second. In a fight the pile pages stay
  at 15 FPS even while cards move (`combat_motion_fps`), and the ambient rates
  drop further while the game itself is dropping frames.
- **Staggered captures.** At most one part starts a render, and one finished
  frame is handed over, per game frame (`stagger_captures`).
- **Native present thread.** The readback callback only copies the frame into
  a mailbox. A thread in `libsts2ds.so` compares it with the last one in bands
  of 16 rows, copies just the changed bands and posts to the window, off the
  game thread (`present_thread`).
- **One rebuild per burst.** Pile changes are debounced (trailing, capped), a
  rebuild waits for the pile's running motion, and at most one pile rebuilds
  per frame. All cards of a change move under one tween, and badge counts are
  applied once per frame.
- **Quiet when idle.** The dock's selected-tab loops settle after one cycle in
  combat, and tabs that aren't shown don't process (`pause_hidden_pages`).
- **Cached reflection.** Game members are resolved once into delegates; node
  paths used by tweens are static.

### Engine and runtime

- **Particle thinning.** New particle emitters draw a share of their particles
  (Particle effects setting), patched where the game adds nodes rather than on
  every node.
- **Combat code warmup.** The compat pack JIT-compiles the combat code while
  the game loads (`preload_combat_code_enabled` in `settings.save`).
- **Room warmups.** As each room loads, its VFX are played once out of sight
  (the compat pack's preload options). Its animation and hit-effect warmups
  are forced off: fights still crashed with them on.
- **Startup warmup cache.** The startup tree warmup is skipped when the
  shader cache and the Vulkan pipeline cache on disk are newer than the game
  build (`warmup cache: HIT` / `MISS` in the log).
- **Pipeline cache saved often.** `override.cfg` next to the game sets
  `rendering_device/pipeline_cache/save_chunk_size_mb=0.25`, since Android
  seldom gives the engine the clean exit that otherwise saves it.
- **GC nursery.** `MONO_GC_PARAMS=nursery-size=32m,major=marksweep-conc` by
  default: fewer minor collections in busy fights.
- **CPU boost hint.** On Android 13+, each frame's work time is reported to
  Android's performance hint API (ADPF) against a one-frame target.
- **Pacing.** The top screen is locked to 60 Hz by default; the FPS limit and
  Swappy frame pacing mode are settings.

## Settings

Launcher settings apply from the next launch. Lower-screen ones are written
to `files/dualscreen.json`; engine ones to `override.cfg`, the environment
or the game's `settings.save`.

| Setting | Key | Default |
|---|---|---|
| Map and piles on the lower screen | `enabled` | on |
| Smoother lower screen | `fps` | 30 (60 when on) |
| Opens on | `default_tab` | map |
| Advanced › Draw on its own thread | `present_thread` | on |
| Advanced › Spread lower-screen updates | `stagger_captures` | on |
| Advanced › Pause hidden tabs | `pause_hidden_pages` | on |
| Particle effects | `particle_amount` | half |
| CPU boost hint | `perf_hint` | on |
| Memory cleanup (GC) | `MONO_GC_PARAMS` | 32 MB nursery |
| Keep compiled shaders | `override.cfg` | on |
| Frame pacing | `override.cfg` | steady |
| FPS limit | `fps_limit` (`settings.save`) | 60 |
| Top screen refresh rate | — | 60 Hz |
| Pre-compile combat code | `preload_combat_code_enabled` | on |
| Pre-warm combat effects | compat pack preload keys | on |
| Use OpenGL instead of Vulkan | `--rendering-method` | off |

### Overrides

The launcher rewrites `dualscreen.json` on every launch.
`files/dualscreen_overrides.json`, if present, is laid over it (same keys; it
wins). It can force any key above, and also takes two that have no setting:

| Key | Default | Range |
|---|---|---|
| `combat_motion_fps` | 15 | 10–60 |
| `canvas_scale` | 1.3 | 1–2 |

The development build (`make dev-apk`) is debuggable, so it can be pushed with
`run-as`:

```sh
echo '{"present_thread": false}' > overrides.json
adb push overrides.json /data/local/tmp/
adb shell run-as dev.sts2ds cp /data/local/tmp/overrides.json files/dualscreen_overrides.json
```

## Measuring

`make logcat` follows the log (tag `STS2DualScreen`).

- **Stats line, every 10 s:** frames posted and unchanged, frame average and
  maximum, `hitches=` (frames over 20 ms) out of all frames, `gc_minor` and
  `gc_major` deltas, pipeline compile deltas (`canvas`, `draw`, `spec`), the
  most capture callbacks in one frame (`captures_cb_max`), the longest
  callback and present (`cb_ms_max`, `present_ms_max`), and `pile_rebuilds`.
  Each part (page, dock, corners) adds a line with its captures, readback and
  render times.
- **`hitch` lines:** one per frame over 20 ms (rate-limited), with that
  frame's compiles, GC counts, capture callbacks and whether a pile rebuild or
  tween started. Together these tell shader, JIT, GC and lower-screen spikes
  apart.
- **Perfetto:** the app is profileable. `libsts2ds` and the C# side add app
  trace sections (submit, lock, copy, post; tick, frame data, present, pile
  refresh). Record with the `gfx`, `view`, `sched` and `freq` categories plus
  the app's atrace. `gfxinfo` doesn't see Godot's SurfaceView; use
  `dumpsys SurfaceFlinger --latency`.

## Limits and ideas

- Every changed lower-screen frame still crosses the CPU (GPU → staging buffer
  → mailbox → shadow → window). Rendering into an `AHardwareBuffer`-backed
  image (`VK_ANDROID_external_memory_android_hardware_buffer`) that the window
  posts directly would remove those copies, at the cost of GPU sync and
  driver risk.
- AOT-compiling `sts2.dll` is not practical: the runtime is a custom Mono
  9.0.7 build, AOT images must match it exactly, and every game update would
  need a recompile.
