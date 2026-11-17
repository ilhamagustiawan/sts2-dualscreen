<p align="center">
  <img src="launcher/app/src/main/res/mipmap-xxxhdpi/ic_launcher_art.png" width="160" alt="STS2 Dual Screen app icon" />
</p>

<h1 align="center">Slay the Spire 2 — Dual Screen</h1>

<p align="center">
  Play <b>Slay the Spire 2</b> on Android across <b>both screens</b> of a dual-screen handheld.<br />
  The fight stays on top; the <b>run map</b> and your <b>card piles</b> move to the lower touchscreen.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
  <img src="https://img.shields.io/badge/Android-8.0%2B%20arm64-brightgreen.svg" alt="Android 8.0+ arm64" />
  <img src="https://img.shields.io/badge/Godot-4.5.1%20Mono-478CBF.svg" alt="Godot 4.5.1 Mono" />
  <img src="https://img.shields.io/badge/Built%20for-AYN%20Thor-orange.svg" alt="Built for AYN Thor" />
</p>

<!--
  Screenshots: add photos of the device to docs/images/ and uncomment.
<p align="center">
  <img src="docs/images/thor-combat.jpg" width="48%" alt="Combat on top, draw pile on the lower screen" />
  <img src="docs/images/thor-map.jpg" width="48%" alt="The run map on the lower screen" />
</p>
-->

> [!IMPORTANT]
> This app contains no part of the game. You need your own copy of
> Slay the Spire 2 on Steam. The app downloads it from your account.

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Install](#install)
- [Steam account and cloud saves](#steam-account-and-cloud-saves)
- [Logs and bug reports](#logs-and-bug-reports)
- [How it works](#how-it-works)
- [Building from source](#building-from-source)
- [Acknowledgements](#acknowledgements)
- [Licence and credits](#licence-and-credits)
- [Legal](#legal)

## Features

- **A second screen for the run**
  - **Map**: the game's own map screen, always shown. Scroll it, read room
    tips and draw on it by touch.
  - **Draw / Discard / Exhaust / Deck**: the game's own pile views, updated
    live. The pile buttons on the top screen switch the lower screen instead
    of covering the fight.
- **Steam built in**: sign in with a QR code or password + Steam Guard, and
  the game downloads straight from your library onto the device. Pick the
  stable or the public beta branch.
- **Steam Cloud saves**: newer saves come down before you play and yours go
  up after. When both sides changed, the app asks which to keep.
- **Controller and touch**: both work. The controller always drives the top
  screen.
- **Tuned for handhelds**: settings for frame pacing, refresh rate, particle
  density, warmups and more. See [docs/PERFORMANCE.md](docs/PERFORMANCE.md).
- **Works on one screen too**: the second display is found through Android's
  display API, not by device model. With one screen the game plays as it
  would anywhere else.

## Requirements

| | |
|---|---|
| **Device** | arm64 Android 8.0 or newer. A second display is optional. Built and tested on the **AYN Thor**. |
| **Game** | Slay the Spire 2 in your Steam library. |
| **Storage** | Room on the device for the game download. |

Tried it on another dual-screen device? Open an issue and say how it went.

## Install

1. Download the APK from the
   [releases page](https://github.com/ilhamagustiawan/sts2-dualscreen/releases)
   or [build it](#building-from-source), then install it:

   ```bash
   adb install -r sts2-dualscreen-<version>.apk
   ```

2. Open **STS2 Dual Screen** and tap **Sign in to Steam**.
3. Tap **Download game**.
4. Tap **Play**.

The first launch prepares the game and takes longer. Later launches start in
seconds.

## Steam account and cloud saves

Sign-in, depot downloads and Steam Cloud go through
[JavaSteam](https://github.com/Longi94/JavaSteam), an open-source Steam client
library. The app talks to Steam directly. Nothing goes through a third-party
server.

- **Your password is never stored.** It is used once to sign in. The app keeps
  only your account name and the login token Steam issues in return. Both are
  encrypted with a key held in the Android Keystore and never leave the device.
- **Signing out** (select the Steam account in the launcher) deletes the token
  and its key. The downloaded game and your saves stay on the device.
- **Cloud saves** sync before and after each session. Conflicts are handled,
  but back up your saves if you want to be sure nothing is lost.

## Logs and bug reports

The launcher's **Logs** screen shows the launcher and game logs, and can share,
save or copy them. They hold no account name, Steam ID or token. Attach them
to an issue along with your device and game version.

The release build records only warnings and failures. For the full log
(startup steps, settings, performance stats), reproduce the problem with the
[dev build](#development).

## How it works

```
LauncherActivity  ──  sign in → download depot → GamePrep → GodotApp
GodotApp (top screen)  ─ custom Godot engine ─ STS2Mobile.dll (our entry shim)
                                                 ├─ Sts2MobileLauncher compat pack
                                                 └─ STS2DualScreen.dll ─┐
DualScreenController ─ Presentation on display 2 ◄─ libsts2ds.so ◄───────┘
```

- **Launcher** (Kotlin): Steam sign-in, the depot download, cloud saves, and
  preparing the game files for the engine.
- **Engine**: the Android Godot 4.5.1 Mono runtime and compatibility pack from
  Sts2MobileLauncher, which make the PC build run on Android.
- **Entry shim** (C#): loads the compatibility pack, then starts the dual-screen
  layer.
- **Dual-screen layer** (C#): builds the map and pile views in the game itself,
  captures them, and hands the frames to a small native library that draws
  them on an Android `Presentation` on the second display. Touches on the lower
  screen are sent back the same way.

Every game type is looked up by name at runtime, so the project builds without
any game files. If an update renames something, that feature stays on the top
screen and the game still runs.

More detail:

- [docs/DUALSCREEN.md](docs/DUALSCREEN.md): the lower-screen design and its
  known limits.
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md): frame costs, every setting and
  override key, and how to measure.

## Building from source

You don't need any game files to build the APK. On macOS:

```bash
make setup     # JDK 21, Android SDK/NDK, .NET 9, GNU bash/coreutils, dex2jar (no sudo)
```

```bash
make apk       # → dist/sts2-dualscreen-<version>.apk
```

```bash
make test
```

`make apk` first runs `tools/runtime/make-runtime.sh`, which downloads the
[Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
release APK (MIT) and takes its prebuilt engine runtime: the custom Godot 4.5.1
Mono engine, Mono, FMOD, spine-godot and the compatibility packs. The Godot Java
library comes from Maven Central.

The APK is signed with the Android debug key. To publish your own build, sign
it with your own key.

### Development

```bash
make dev-apk   # → dist/sts2-dualscreen-<version>-dev.apk, debuggable
```

```bash
make dev       # dev-apk, install, launch, follow the log
```

```bash
make logcat    # follow the log
```

```bash
make compat COMPAT_REF=<dir with sts2.dll>   # rebuild one compat pack target from source
```

The dev build is debuggable, so `adb shell run-as dev.sts2ds` reaches the
app's files (logs, saves, `dualscreen_overrides.json`). Both builds share a
package name but not a debuggable flag: uninstall one before installing the
other.

> [!WARNING]
> A debuggable build lets any computer you have authorised for ADB read the
> app's files, including the encrypted Steam token. Play on the release build,
> and only use the dev build on a device and computer you trust.

## Acknowledgements

This project stands on the work of others. Thank you to:

- **[jakobkhansen](https://github.com/jakobkhansen)** for
  [SilksongAndroid](https://github.com/jakobkhansen/SilksongAndroid), the
  Hollow Knight: Silksong port with dual-screen support that showed this could
  be done on the AYN Thor. Its launcher, Steam sign-in, depot download, cloud
  saves and second-screen touch handling are the base this app grew from.
- **[ModinMobileSTS](https://github.com/ModinMobileSTS)** for
  [Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
  and its Android compatibility pack. Their custom engine runtime is what lets
  Slay the Spire 2 run on Android at all, and the dual screen is built on top
  of it.
- **[JavaSteam](https://github.com/Longi94/JavaSteam)** for the Steam client,
  depot downloader and cloud storage.
- **[Godot Engine](https://godotengine.org)**, **[.NET / Mono](https://github.com/dotnet/runtime)**
  and **[Harmony](https://github.com/pardeike/Harmony)**, which the game and the
  compatibility layer are built on.
- **[ZXing](https://github.com/zxing/zxing)** (QR sign-in),
  **[OkHttp](https://github.com/square/okhttp)**,
  **[Bouncy Castle](https://www.bouncycastle.org)**,
  **[Protocol Buffers](https://github.com/protocolbuffers/protobuf)**,
  **[zstd-jni](https://github.com/luben/zstd-jni)** and
  **[XZ for Java](https://tukaani.org/xz/java.html)**.
- **[Mega Crit](https://www.megacrit.com)** for Slay the Spire 2. Please buy
  the game.

## Licence and credits

This project is MIT-licensed. See [LICENSE](LICENSE).

Code adapted from other projects:

- [SilksongAndroid](https://github.com/jakobkhansen/SilksongAndroid) (MIT) by
  jakobkhansen: the launcher, Steam sign-in, depot download, cloud saves and
  the second-screen touch handling.
- [Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)
  (MIT) by ModinMobileSTS: the Android engine runtime, its compatibility pack
  and launch staging rules.

The engine runtime taken from the Sts2MobileLauncher release at build time
includes FMOD and spine-godot, which are under their own licences, not MIT.

## Legal

- **Unofficial.** This is a fan project. It is not affiliated with or endorsed
  by Mega Crit. Slay the Spire 2 is © Mega Crit.
- **No game content.** Neither this repository nor the APK contains any of the
  game's code, art, audio or data. The app downloads the game from the Steam
  account of a player who owns it, onto that player's device.
- **Your own copy.** Don't share APKs with the game bundled in.
