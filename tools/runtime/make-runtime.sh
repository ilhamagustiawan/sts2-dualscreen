#!/usr/bin/env bash
# Build the Android runtime for sts2-dualscreen.
#
# Slay the Spire 2 only boots on a custom Godot 4.5.1 Mono engine (plus FMOD,
# spine-godot and a matching Mono). None of that is published as source, so we
# take the prebuilt runtime from a Sts2MobileLauncher release APK (MIT) and
# combine it with the public Godot 4.5.1 Java library from Maven Central.
#
#   STS2RE_TAG   Sts2MobileLauncher release to take the runtime from (default: v0.1.13)
#   STS2RE_APK   use this local APK instead of downloading
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
TAG="${STS2RE_TAG:-v0.1.13}"
GODOT_VERSION="4.5.1.stable"
CACHE="$ROOT/.cache/runtime/$TAG"
APP="$ROOT/launcher/app"
LIBS="$APP/libs"
JNI="$APP/src/main/jniLibs/arm64-v8a"
ASSETS="$APP/src/main/assets"
export JAVA_HOME="${JAVA_HOME:-$(/usr/libexec/java_home -v 21 2>/dev/null || true)}"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
ANDROID_JAR="$ANDROID_HOME/platforms/android-35/android.jar"

die() { echo "make-runtime: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "missing '$1' (run tools/setup-mac.sh)"; }
need unzip; need zip; need curl; need d2j-dex2jar
[[ -x "$JAVA_HOME/bin/javac" ]] || die "JAVA_HOME has no javac: $JAVA_HOME"
[[ -f "$ANDROID_JAR" ]] || die "missing $ANDROID_JAR"
sha() { shasum -a 256 "$1" | cut -d' ' -f1; }

mkdir -p "$CACHE"

# 1. Sources ------------------------------------------------------------------
APK="${STS2RE_APK:-$CACHE/sts2re.apk}"
if [[ ! -f "$APK" ]]; then
  echo "Downloading Sts2MobileLauncher $TAG release APK..."
  url="$(curl -fsSL "https://api.github.com/repos/ModinMobileSTS/Sts2MobileLauncher/releases/tags/$TAG" |
    python3 -c 'import json,sys; print(next(a["browser_download_url"] for a in json.load(sys.stdin)["assets"] if a["name"].endswith(".apk")))')"
  curl -fL --progress-bar -o "$APK.part" "$url"
  mv "$APK.part" "$APK"
fi

GODOT_AAR_SRC="$CACHE/godot-$GODOT_VERSION.aar"
if [[ ! -f "$GODOT_AAR_SRC" ]]; then
  echo "Downloading Godot $GODOT_VERSION Android library..."
  curl -fL --progress-bar -o "$GODOT_AAR_SRC.part" \
    "https://repo1.maven.org/maven2/org/godotengine/godot/$GODOT_VERSION/godot-$GODOT_VERSION.aar"
  mv "$GODOT_AAR_SRC.part" "$GODOT_AAR_SRC"
fi

X="$CACHE/apk"
rm -rf "$X" && mkdir -p "$X"
unzip -q "$APK" 'lib/arm64-v8a/*' 'assets/dotnet_bcl/*' 'assets/compat_packs/*' \
  'assets/bootstrap.pck' 'assets/port_compat.pck' 'classes*.dex' -d "$X"

# 2. Native libraries -----------------------------------------------------------
# libgodot_android.so goes inside the Godot AAR; Sts2MobileLauncher's own
# workshop/zstd helpers are not used by this app.
rm -rf "$JNI" && mkdir -p "$JNI"
for so in "$X"/lib/arm64-v8a/*.so; do
  name="$(basename "$so")"
  case "$name" in
    libgodot_android.so|libworkshop_zstd.so|libzstd.so) continue ;;
  esac
  cp "$so" "$JNI/"
done

# 3. Godot AAR with the custom engine -----------------------------------------
mkdir -p "$LIBS"
AAR="$LIBS/godot-lib.template_release.aar"
cp "$GODOT_AAR_SRC" "$AAR"
STAGE="$CACHE/aar-stage"
rm -rf "$STAGE" && mkdir -p "$STAGE/jni/arm64-v8a"
cp "$X/lib/arm64-v8a/libgodot_android.so" "$STAGE/jni/arm64-v8a/"
# The custom engine is arm64 only; drop the stock engine for other ABIs.
zip -q -d "$AAR" 'jni/armeabi-v7a/*' 'jni/x86/*' 'jni/x86_64/*' >/dev/null 2>&1 || true
(cd "$STAGE" && zip -q -u "$AAR" jni/arm64-v8a/libgodot_android.so)

# Godot 4.5.1 leaks a pooled input event per unsupported mouse action; once
# the pool (1200) is empty, the game ignores all input. Upstream's repair
# (Sts2MobileLauncher tools/android, MIT), plus its ordered
# input-source bridge for the compat pack's touch/controller policy.
INPUT_PATCH="$ROOT/tools/runtime/godot-input/patch-godot-input-pool.py"
python3 "$INPUT_PATCH" "$AAR" --javac "$JAVA_HOME/bin/javac" --android-jar "$ANDROID_JAR"
python3 "$INPUT_PATCH" "$AAR" --check

# 4. Java classes the natives call into (FMOD, .NET crypto) -------------------
J="$CACHE/java"
rm -rf "$J" && mkdir -p "$J/classes" "$J/shim"
for dex in "$X"/classes*.dex; do
  d2j-dex2jar -f -o "$J/$(basename "$dex" .dex).jar" "$dex" >/dev/null 2>&1 || true
done
for jar in "$J"/*.jar; do
  (cd "$J/classes" && unzip -q -o "$jar" 'org/fmod/*' 'net/dot/*' 2>/dev/null || true)
done
[[ -f "$J/classes/org/fmod/AudioDevice.class" ]] || die "org.fmod classes not found in $APK"
# Sts2MobileLauncher's FMOD shim (device enumeration + AAudio fixes) replaces FMOD.class.
"$JAVA_HOME/bin/javac" -source 17 -target 17 -nowarn -cp "$ANDROID_JAR" \
  -d "$J/shim" "$ROOT/tools/runtime/FMOD.java"
rm -f "$J"/classes/org/fmod/FMOD*.class
cp -R "$J/shim/org" "$J/classes/"
rm -f "$LIBS/runtime-java.jar"
(cd "$J/classes" && zip -q -r "$LIBS/runtime-java.jar" .)

# 5. Managed runtime + compat packs ------------------------------------------
rm -rf "$ASSETS/dotnet_bcl" "$ASSETS/compat_packs"
mkdir -p "$ASSETS"
cp -R "$X/assets/dotnet_bcl" "$ASSETS/dotnet_bcl"
cp -R "$X/assets/compat_packs" "$ASSETS/compat_packs"
cp "$X/assets/bootstrap.pck" "$X/assets/port_compat.pck" "$ASSETS/"

# 6. Record what we used ------------------------------------------------------
{
  echo "{"
  echo "  \"sts2re_tag\": \"$TAG\","
  echo "  \"sts2re_apk_sha256\": \"$(sha "$APK")\","
  echo "  \"godot_aar\": \"$GODOT_VERSION\","
  echo "  \"godot_input_patch_sha256\": \"$(unzip -p "$AAR" classes.jar | python3 -c 'import io,json,sys,zipfile; print(json.loads(zipfile.ZipFile(io.BytesIO(sys.stdin.buffer.read())).read("META-INF/sts2-input-source.json"))["source_sha256"])')\","
  echo "  \"libgodot_android_sha256\": \"$(sha "$X/lib/arm64-v8a/libgodot_android.so")\","
  echo "  \"libmonosgen_sha256\": \"$(sha "$JNI/libmonosgen-2.0.so")\","
  echo "  \"libfmod_sha256\": \"$(sha "$JNI/libfmod.so")\""
  echo "}"
} > "$ASSETS/runtime_manifest.json"

echo "Runtime ready:"
echo "  $AAR"
echo "  $LIBS/runtime-java.jar ($(unzip -l "$LIBS/runtime-java.jar" | tail -1 | awk '{print $2}') files)"
echo "  $JNI ($(ls "$JNI" | wc -l | tr -d ' ') libraries)"
echo "  $ASSETS/{dotnet_bcl,compat_packs,bootstrap.pck,port_compat.pck}"
