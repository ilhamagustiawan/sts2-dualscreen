# Slay the Spire 2 — Dual Screen

Play **Slay the Spire 2** on Android, across **both screens** of a dual-screen
handheld. The game runs on the top screen; the **run map** and your **draw,
discard, exhaust and deck piles** live on the lower touchscreen.

Built for the **AYN Thor**. The second screen is found through Android's
display API, not by device model, and with one screen the game plays as it
would anywhere else.

> This app contains no part of the game. You need your own copy of
> Slay the Spire 2 on Steam; the app downloads it from your account.

## What it does

- **Steam sign-in** (QR code or password + Steam Guard), then the game is
  downloaded straight from your Steam library onto the device.
- **Steam Cloud saves**: download before playing, upload after, or both
  automatically.
- **Lower screen**
  - **Map**: the game's own map screen. It opens down here at the end of a
    room and when you tap the map button; choose your path with a tap.
  - **Draw / Discard / Exhaust / Deck**: the game's own pile views, updated
    live. The pile buttons on the top screen switch the lower screen instead
    of covering the fight.
- **Controller and touch** both work; the controller always drives the top
  screen.

## Install

1. Build the APK (below) and install it: `adb install -r dist/sts2-dualscreen-*.apk`.
2. Open **STS2 Dual Screen**, **Log in to Steam**, then **Download game**.
3. **Launch game**.

## Building

On macOS:

```bash
make setup     # JDK 21, Android SDK/NDK, .NET 9, GNU bash/coreutils, dex2jar (no sudo)
make apk       # → dist/sts2-dualscreen-<version>.apk
make dev       # build, install, launch, follow the log
make test
```

`make apk` first runs `tools/runtime/make-runtime.sh`, which downloads the
[Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
release APK (MIT) and takes its prebuilt engine runtime: the custom Godot 4.5.1
Mono engine, Mono, FMOD, spine-godot and the compatibility packs. The Godot Java
library comes from Maven Central. No game files are needed to build.

## How it works

```
LauncherActivity  ──  sign in → download depot → GamePrep → GodotApp
GodotApp (top screen)  ─ custom Godot engine ─ STS2Mobile.dll (our entry shim)
                                                 ├─ Sts2MobileLauncher compat pack
                                                 └─ STS2DualScreen.dll ─┐
DualScreenController ─ Presentation on display 2 ◄─ libsts2ds.so ◄───────┘
```

See [docs/DUALSCREEN.md](docs/DUALSCREEN.md) for the design and
[NOTICE.md](NOTICE.md) for what came from where.

## Licence and credits

MIT — see [LICENSE](LICENSE).

- [SilksongAndroid](https://github.com/jakobkhansen/SilksongAndroid) (MIT) by
  jakobkhansen: the launcher, Steam sign-in, depot download, cloud saves and
  the second-screen touch handling are adapted from it.
- [Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
  (MIT) by ModinMobileSTS: the Android engine runtime, its compatibility pack
  and launch staging rules.

Slay the Spire 2 is © Mega Crit. This is an unofficial fan project, not
affiliated with or endorsed by Mega Crit.
