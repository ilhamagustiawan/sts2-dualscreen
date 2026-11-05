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
  downloaded straight from your Steam library onto the device. Stable or the
  public beta branch.
- **Steam Cloud saves**: newer saves are downloaded before you play and yours
  uploaded after, with a prompt when both sides changed.
- **Lower screen**
  - **Map**: the game's own map screen, always shown. Scroll it, read room
    tips and draw on it by touch.
  - **Draw / Discard / Exhaust / Deck**: the game's own pile views, updated
    live. The pile buttons on the top screen switch the lower screen instead
    of covering the fight.
- **Controller and touch** both work; the controller always drives the top
  screen.
- **Tuned for handhelds**: settings for frame pacing, refresh rate, particle
  density, warmups and more. See [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Requirements

- An arm64 Android 8.0+ device. A second display is optional; the AYN Thor is
  the one it is built and tested on.
- Slay the Spire 2 in your Steam library, and room on the device for the game.

## Install

1. Download the APK from the releases page, or build it (below), and install
   it: `adb install -r dist/sts2-dualscreen-<version>.apk`.
2. Open **STS2 Dual Screen**, **Log in to Steam**, then **Download game**.
3. **Launch game**.

The first launch prepares the game and takes longer; later ones start in
seconds.

## Building

On macOS:

```bash
make setup     # JDK 21, Android SDK/NDK, .NET 9, GNU bash/coreutils, dex2jar (no sudo)
make apk       # → dist/sts2-dualscreen-<version>.apk
make test
```

`make apk` first runs `tools/runtime/make-runtime.sh`, which downloads the
[Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
release APK (MIT) and takes its prebuilt engine runtime: the custom Godot 4.5.1
Mono engine, Mono, FMOD, spine-godot and the compatibility packs. The Godot Java
library comes from Maven Central.

It then rebuilds your game version's compatibility pack from source with the
patches in `tools/runtime/compat-patches/` (the combat VFX pool). That needs
your own `sts2.dll` once, pulled from the device after the game downloads:

```bash
mkdir -p g && adb pull /sdcard/Android/data/dev.sts2ds/files/depot/data_sts2_windows_x86_64/sts2.dll g/
make compat COMPAT_REF=g   # saved under .cache; later builds reuse it
```

`make apk COMPAT=release` skips this and ships the released pack as is.

The APK is signed with the Android debug key. To publish your own build, sign
it with your own key.

### Development

```bash
make dev-apk   # → dist/sts2-dualscreen-<version>-dev.apk, debuggable
make dev       # dev-apk, install, launch, follow the log
make logcat    # follow the log
make compat COMPAT_REF=<dir with sts2.dll>   # rebuild the game's compat target with the VFX pool
```

The dev build is debuggable, so `adb shell run-as dev.sts2ds` reaches the
app's files (logs, saves, `dualscreen_overrides.json`). Both builds share a
package name but not a debuggable flag: uninstall one before installing the
other.

## Logs and bug reports

The launcher's **Log** screen shows the launcher and game logs and can share,
save or copy them. They hold no account name, Steam ID or token. Attach them
to an issue together with your device and game version.

The release build records warnings and failures only. For the full log
(startup steps, settings, performance stats), reproduce with the dev build.

## How it works

```
LauncherActivity  ──  sign in → download depot → GamePrep → GodotApp
GodotApp (top screen)  ─ custom Godot engine ─ STS2Mobile.dll (our entry shim)
                                                 ├─ Sts2MobileLauncher compat pack
                                                 └─ STS2DualScreen.dll ─┐
DualScreenController ─ Presentation on display 2 ◄─ libsts2ds.so ◄───────┘
```

See [docs/DUALSCREEN.md](docs/DUALSCREEN.md) for the design and its known
limits, and [docs/PERFORMANCE.md](docs/PERFORMANCE.md) for performance and its
settings.

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
