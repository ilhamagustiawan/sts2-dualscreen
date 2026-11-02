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
5. Controller and keyboard input only reaches the root window. The game turns
   controller buttons into `ui_*` actions with `Input.ParseInputEvent`. While
   the open map is the game's current screen (`ActiveScreenContext`), every
   non-pointer event the root gets (`Window.WindowInput`) is also pushed into
   the SubViewport. Map focus, A to travel, and the controller drawing cursor
   then work as on one screen. Top-screen focus is released while the map has
   the controller.

The tab bar is docked along the bottom edge of the lower screen.

## Capture pipeline

- **Pacing.** A part renders (`UpdateMode.Once`) only when a capture is due:
  at the active rate while something on it moves (`MarkChanged`, or the last
  capture differed, for 0.25 s), at an ambient rate while something loops
  forever, else once a second to catch changes nobody announced. In a fight
  the pile pages stay at 15 FPS even while their cards move
  (`combat_motion_fps`), and lower still while the game is dropping frames.
- **Stagger.** At most one part starts a render per game frame (the one that
  has waited longest; the pages first under a finger), and at most one frame
  is handed to libsts2ds per game frame; one that comes back later in the same
  frame is held for the next.
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
- **Switches.** The launcher rewrites `dualscreen.json` on every launch. The
  capture switches it has no setting for go in `files/dualscreen_overrides.json`
  (same keys, laid over it): `present_thread`, `stagger_captures`,
  `pause_hidden_pages`, `combat_motion_fps`. For example:

  ```sh
  echo '{"present_thread": false}' > overrides.json
  adb push overrides.json /data/local/tmp/
  adb shell run-as dev.sts2ds cp /data/local/tmp/overrides.json files/dualscreen_overrides.json
  ```

## What is on the lower screen

| Tab | Content | Hook |
|---|---|---|
| Map | the game's own `NMapScreen`, moved here, always shown | `NMapScreen._Ready` (reparent), `Open`/`Close` (switch tab, hint, controller), `AnimClose` skipped |
| Draw / Discard / Exhaust | a fresh `NCardPileScreen` bound to the live `CardPile` | `NCombatCardPile.Initialize(Player)`; `NCardPileScreen.ShowScreen` redirected |
| Deck | same, with `PileType.Deck` | `NTopBarDeckButton.Initialize(Player)`; `NDeckViewScreen.ShowScreen` redirected |

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
  still reach it.

## Known limits / next

- The closed map stays visible on the Map tab. Its close fade-out is skipped,
  and `Close(false)` is undone. Touch can scroll it. Tapping a travelable point
  opens the map first, then travels. The controller stays on the top screen
  until the game opens the map.
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
