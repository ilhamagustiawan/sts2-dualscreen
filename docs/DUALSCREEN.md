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
3. `DsBottomScreen` (C#) renders the lower-screen UI into a `SubViewport` sized
   to that surface, with a canvas 1.3× the panel so the game's 1080p UI stays
   readable. Every frame, capped at 30 or 60 FPS, it reads the viewport back
   and calls `ds_submit_frame`, which skips unchanged frames (sampled hash)
   and otherwise copies into the window.
4. Touches on the surface are queued natively and drained by C# each frame,
   then pushed into the SubViewport as mouse events. On pile tabs a vertical
   drag also produces wheel steps, because the game's card grids scroll by wheel.

## What is on the lower screen

| Tab | Content | Hook |
|---|---|---|
| Map | the game's own `NMapScreen`, moved here | `NMapScreen._Ready` (reparent), `Open`/`Close` (switch tab, top-screen hint) |
| Draw / Discard / Exhaust | a fresh `NCardPileScreen` bound to the live `CardPile` | `NCombatCardPile.Initialize(Player)`; `NCardPileScreen.ShowScreen` redirected |
| Deck | same, with `PileType.Deck` | `NTopBarDeckButton.Initialize(Player)`; `NDeckViewScreen.ShowScreen` redirected |

Every game type is looked up by name. A missing type or method logs
`hook skipped` and leaves that feature on the top screen.

## Lifecycle

- Activity `onStop` hides the presentation (not focus loss: touching the other
  screen moves focus while the game is still visible), `onStart` brings it back.
- Display added or removed (the Thor can turn its lower screen off): re-evaluated.
- No surface: the SubViewport stops rendering and the top-screen hint hides.

## Known limits / next

- While the map is closed, the Map tab shows "Tap to view the map" rather than
  a live preview: the game's map screen fades out and stops processing when
  closed, and forcing it visible would fight that.
- Readback is synchronous (`Viewport.GetTexture().GetImage()`). The log reports
  `readback_avg`; if it is high on device, switch to
  `RenderingDevice.TextureGetDataAsync`.
- Hover tooltips from cards on the lower screen are drawn by the game's global
  hover-tip layer, which is on the top screen.
