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
- Readback is synchronous (`Viewport.GetTexture().GetImage()`). The log reports
  `readback_avg`; if it is high on device, switch to
  `RenderingDevice.TextureGetDataAsync`.
- Hover tooltips from cards on the lower screen are drawn by the game's global
  hover-tip layer, which is on the top screen.
