# Dual screen design

## Why frames are pushed

Unity (SilksongAndroid) can render a camera straight to a second display.
Godot on Android cannot: its display server owns exactly one window. So the
bottom screen is not a Godot window at all:

1. `DualScreenController` (Java) finds a presentation display other than the
   activity's own and shows `DsPresentation` on it: one `SurfaceView`, not
   focusable (so gamepad input stays on the game), locked landscape (the Thor's
   lower panel is natively portrait).
2. Its surface goes to `libsts2ds.so` as an `ANativeWindow`.
3. `DsBottomScreen` (C#) renders the lower-screen UI into `SubViewport`s sized
   to that surface, with a canvas 1.3× the panel so the game's 1080p UI stays
   readable: the pages, the dock strip and the two corner tabs, each captured
   on its own (`DsCapture`) and only when due (see "Capture pipeline").
4. Touches on the surface are queued natively and drained by C# each frame,
   then pushed into the SubViewport as mouse events in canvas coordinates. On
   pile tabs a vertical drag also produces wheel steps, because the game's card
   grids scroll by wheel. Two fingers always scroll: the first finger's press is
   released, and the midpoint's vertical movement becomes pan gestures on the
   map (which also scrolls in draw mode) or wheel steps on the piles.
5. Controller and keyboard input stays with the game on the top screen. The
   lower screen is touch only: each touch runs with the game in touch mode,
   even while the top is in controller mode (`BeginTouchDispatch`). The root's
   events (`Window.WindowInput`) are only noted, to tell a controller press
   from a touch.

The tab bar is docked along the bottom edge of the lower screen.

## Capture pipeline

- **Pacing.** A part renders (`UpdateMode.Once`) only when a capture is due:
  at the active rate while something on it moves (`MarkChanged`, or the last
  capture differed, for 0.25 s), at an ambient rate while something loops
  forever, else once a second to catch changes nobody announced. A window
  owes one more capture after it ends, so the frame a motion ends on is
  always drawn. In a fight
  the pile pages stay at 15 FPS even while their cards move
  (`combat_motion_fps`), and lower still while the game is dropping frames.
- **Stagger.** At most one part starts a render per game frame (the one that
  has waited longest; the pages first under a finger, unless another part has
  been owed a frame for 0.25 s or its window is over), and at most one frame
  is handed to libsts2ds per game frame; one that comes back later in the same
  frame is held for the next, except the pages' while a finger is on the
  lower screen or they scroll.
- **Readback.** Asynchronous: `RenderingDevice.TextureGetDataAsync`. Its
  callback runs on the game's thread inside the engine's frame and reads the
  frame in place (no managed copy); the stats line times all of it
  (`cb_ms_max`).
- **Native side.** With `present_thread` on (the default), the callback's
  only work is one copy into a mailbox; a thread in libsts2ds compares the
  frame with what that part last drew, in bands of 16 rows, composes the
  changed bands (and the corner overlays) into a shadow of the surface, locks
  the window for just the changed rows and posts once per game frame. Off,
  the same runs on the game's thread inside the call. A new or destroyed
  surface (`nativeSetSurface`) waits for a post in progress, so a destroyed
  surface is never touched.
- **Measuring.** Every 10 s the log has a stats line (captures, render times,
  pipeline compiles, GC, callback and present times, pile rebuilds), and a
  `hitch` line for any game frame over 20 ms (rate-limited). libsts2ds and
  `DsBottomScreen.TraceBegin`/`TraceEnd` add app trace sections for Perfetto.
- **Switches.** The launcher rewrites `dualscreen.json` on every launch.
  `present_thread`, `stagger_captures` and `pause_hidden_pages` (all on by
  default) are launcher settings, under Advanced > Lower screen.
  `files/dualscreen_overrides.json`, if there, is laid over `dualscreen.json`
  (same keys; it wins) by both `DsConfig` and `DsRuntimeConfig`, for keys
  with no setting (`combat_motion_fps`, `canvas_scale`) or to force one
  (`perf_hint`, the switches above). For example:

  ```sh
  echo '{"present_thread": false}' > overrides.json
  adb push overrides.json /data/local/tmp/
  adb shell run-as dev.sts2ds cp /data/local/tmp/overrides.json files/dualscreen_overrides.json
  ```

## What is on the lower screen

| Tab | Content | Hook |
|---|---|---|
| Map | a second `NMapScreen` from the game's scene, always shown; the top map's state (run, act map, marker, travel state, drawings) is mirrored into it | `NMapScreen._Ready`/`Initialize`/`SetMap`/`InitMarker`/`SetTravelEnabled` (mirror), `NMapPoint.OnRelease` (no travel from the copy) |
| Draw / Discard / Exhaust | a fresh `NCardPileScreen` bound to the live `CardPile` | `NCombatCardPile.Initialize(Player)`; the top's pile buttons open the game's own overlays as usual |
| Deck | same, with `PileType.Deck` | `NTopBarDeckButton.Initialize(Player)` |

Every game type is looked up by name. A missing type or method logs
`hook skipped` and leaves that feature on the top screen.

## Lifecycle

- Activity `onStop` hides the presentation (not focus loss: touching the other
  screen moves focus while the game is still visible), `onStart` brings it back.
- Display added or removed (the Thor can turn its lower screen off): re-evaluated.
- No surface: the SubViewport stops rendering and the top-screen hint hides.
- Pages not shown don't process (`pause_hidden_pages`): the map copy's
  pulsing rooms and the grids' `_Process` and tweens wait until their tab is
  shown again. Mirroring into the map copy is plain calls and signals, which
  still reach it. A pile's card motion is run to its end as its tab is left,
  and a pile changed while hidden rebuilds when its tab opens.

## Known limits / next

- The map tab is a copy: touch scrolls it, shows room tips and draws, but
  tapping a travelable room doesn't travel (`MapPointReleasePrefix` skips the
  copy's `NMapPoint.OnRelease`). Travel is the top map's, by touch or
  controller there.
- Map drawing: the draw button picks a mouse or controller tool from the game's
  input mode. Touches on the lower screen don't reach `NControllerManager`, so
  a button pressed by touch gets the mouse tool (`NMapDrawingInput.Create`).
  The controller cursor's clamp to the top screen's rect is replaced by the
  lower canvas.
- The compat pack's touch "tap to preview" for hand cards
  (`NPlayerHand.OnHolderPressed` prefix) ignores where a press came from. On a
  controller, A would pin a card and then put it back, never playing it. Its
  prefix is unpatched and re-run through ours only when the game isn't in
  controller mode (log: `tap preview limited to touch`).
- The game's "Controller detected" / "Mouse detected" toast
  (`NControllerManager.ControlModeChanged`) is not shown. The top-screen hint
  copies that toast label's font and outline, and the tabs use its font.
- Every changed frame still crosses the CPU (GPU → staging buffer → mailbox
  → shadow → window). Rendering into an `AHardwareBuffer` the window can post
  directly would remove those copies (see docs/PERF-PLAN.md, 3.6).
- Hover tooltips from cards on the lower screen are drawn by the game's global
  hover-tip layer, which is on the top screen.
