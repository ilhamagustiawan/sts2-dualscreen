# What came from where

## SilksongAndroid — MIT

<https://github.com/jakobkhansen/SilksongAndroid>, `src/SilksongLauncher.Launcher/app/src/main`.

Copied with the package renamed to `dev.sts2ds.launcher`, then adapted:

| File here | Changes |
|---|---|
| `SteamSession.kt`, `TokenStore.kt`, `QrAuth.kt`, `QrRenderer.kt`, `CredentialsAuth.kt`, `SteamCrypto.kt`, `SteamCloudClient.kt`, `CloudCompression.kt`, `LoginActivity.kt`, `LogActivity.kt`, `LauncherLog.kt` | names, log tags, game log locations |
| `DepotFetcher.kt` | retargeted to STS2 (app 2868840, Windows depot 2868841) |
| `CloudSync.kt` | transfer machinery kept; save-path mapping rewritten for STS2 (`Sts2SavePaths`) |
| `LauncherActivity.kt` | conflict prompt kept; layout, download and launch rewritten |
| `SaveSync.kt` | the cloud pull/push flows from `LauncherActivity.kt`, moved out; when to prompt (`SyncPlan`) is new |
| `SettingsStore.kt`, `SettingsActivity.kt`, `Sts2App.kt`, `GameReset.kt` | same shape, STS2 settings |
| `res/layout/activity_login.xml`, `res/drawable/focus_on_*.xml`, `res/xml/game_mode_config.xml`, login strings | as-is (palette re-tinted) |
| `dualscreen/DsPresentation.java` touch handling, `cpp/sts2ds.c` touch queue | ported from `SecondaryDisplay.java` (`onTouch`, `TouchBuffer`) |

## Sts2MobileLauncher — MIT

<https://github.com/ModinMobileSTS/Sts2MobileLauncher>, tag `v0.1.11`.

| Here | From |
|---|---|
| Runtime in the APK (`libgodot_android.so`, Mono, FMOD, spine-godot, `dotnet_bcl/`, compat packs, `bootstrap.pck`, `port_compat.pck`) | extracted from the v0.1.11 release APK by `tools/runtime/make-runtime.sh` |
| `tools/runtime/FMOD.java` | `tools/android/fmod-shim/org/fmod/FMOD.java`, unchanged |
| `launcher/.../PckPatcher.java` | `android/src/com/godot/game/PckPatcher.java`, package and log tag changed |
| `launcher/gradlew`, `gradle/wrapper/*` | `android/gradlew`, `android/gradle/wrapper/*` |
| `GamePrep.kt` | staging rules and texture-cache invalidation of `GameLaunchPreparationManager.java` and the launch context of `LaunchProfileManager.java`, reimplemented |
| `GodotApp.java` | library load order and bridge method names of `GodotApp.java`, reimplemented |
| `GameSettingsFile.kt` | `settings.save` performance keys, their defaults, the Recommended graphics preset and the vector format of `ExtraSettingsRepository.java`; the warmup set is the `--preload aggressive` profile of `tools/debug/sts2-adb-debug.sh` (commit `11025d4`) |
| `RefreshRate.java` | mode choice of `HighRefreshRateController.java` (commit `11025d4`), trimmed to one window request and a surface vote |
| `GodotApp.java`, `GamePrep.kt` | `--rendering-method gl_compatibility` of `RendererPreference.java`; the `launcher/enable_debug_menu.flag` overlay switch of `GodotApp.java` (commit `11025d4`) |

The compat pack inside the runtime (`ModinMobileSTS/sts2-android-compat`) is
used unmodified, as shipped in that release.

## Not included

No Slay the Spire 2 code, art, audio or data. The launcher's look follows the
game's main menu in colour only: its backdrop, spire and ornaments are drawn
here as vectors, and the app icon (`mipmap-*/ic_launcher_art.png`) is an
original image generated for this project. The game is downloaded from the
user's own Steam account on the device.
