# sts2-dualscreen

Android app that runs the user's own Slay the Spire 2 (Godot 4.5.1 C#) on a
dual-screen handheld (AYN Thor), with the map and card piles on the lower screen.

## Layout

- `launcher/` — Gradle project (`:app`, package `dev.sts2ds`).
  - `kotlin/dev/sts2ds/launcher/` — launcher, Steam (JavaSteam), download, cloud saves, `GamePrep`.
  - `java/com/godot/game/GodotApp.java` — the game activity. The class name is
    required: the compat pack calls static methods on it via JavaClassWrapper.
  - `java/dev/sts2ds/dualscreen/` — Presentation on the second display.
  - `cpp/sts2ds.c` — surface blit + touch queue; C# P/Invokes `ds_*`.
- `managed/STS2DsEntry` — assembly **named STS2Mobile**: the engine's hard-wired
  entry. Loads the compat pack (`STS2AndroidPortCompat.dll`) in its own
  AssemblyLoadContext, forwards both entry points, then starts STS2DualScreen.
- `managed/STS2DualScreen` — bottom screen. Game types by name via
  `AccessTools`; no compile-time sts2.dll. No Godot subclasses (no source
  generators): use `SceneTree.ProcessFrame` and signals.
- `tools/runtime/make-runtime.sh` — runtime from the Sts2MobileLauncher release APK.
- `scripts/build.sh` — test + build; `scripts/release.sh` — GitHub release
  from this machine (bump, build, signer check, push, upload).
- `docs/DUALSCREEN.md` — lower-screen design; `docs/PERFORMANCE.md` — frame
  costs, the settings and override keys, how to measure. Keep both current.

## Rules

- Sources are only SilksongAndroid and Sts2MobileLauncher (both MIT). Do not
  use Ekyso/StS2-Launcher or balatro-dualscreen. Credit anything copied in the
  README's "Licence and credits".
- Never commit game files or the extracted runtime (see .gitignore). The
  one exception is the key art behind the launcher (`bg_start.webp`).
- `make apk` (not debuggable, for playing), `make dev-apk` (debuggable, for
  `run-as`), `make debug-apk` (dev plus per-event logging), `make test`;
  `make dev` needs a device on adb.
- Log levels: Warn (release), Info (devel), Debug (debug); see
  `docs/DUALSCREEN.md`. No new log line for a success whose failure already warns.
- Public repo: no internal notes, plan numbers or commit hashes in code or
  docs (upstream pins such as the compat commit in `make-compat.sh` are the exception).
