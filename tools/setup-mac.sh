#!/usr/bin/env bash
# Install the macOS toolchain needed to build sts2-dualscreen.
#   JDK 21, Android SDK (platform 35, build-tools 35.0.0, NDK 28.1, CMake 3.22.1),
#   .NET 9 SDK, GNU bash + coreutils (scripts use bash 4 features), dex2jar.
set -euo pipefail

need_brew() { command -v brew >/dev/null || { echo "Homebrew is required: https://brew.sh" >&2; exit 1; }; }
need_brew

# Everything here installs without sudo (no .pkg installers).
brew install bash coreutils dex2jar openjdk@21
brew install --cask android-commandlinetools

export JAVA_HOME="$(brew --prefix openjdk@21)/libexec/openjdk.jdk/Contents/Home"

# .NET 9 SDK into ~/.dotnet (the dotnet-sdk cask needs sudo).
if ! "$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep -q '^9\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet"
fi
SDK="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
mkdir -p "$SDK"
SDKMANAGER="$(brew --prefix)/share/android-commandlinetools/cmdline-tools/latest/bin/sdkmanager"

(yes || true) | "$SDKMANAGER" --sdk_root="$SDK" --licenses >/dev/null || true
"$SDKMANAGER" --sdk_root="$SDK" \
  "platform-tools" \
  "platforms;android-35" \
  "build-tools;35.0.0" \
  "ndk;28.1.13356709" \
  "cmake;3.22.1"

cat <<MSG

Toolchain installed. Add to your shell profile:

  export JAVA_HOME="$JAVA_HOME"
  export ANDROID_HOME="$SDK"
  export PATH="\$HOME/.dotnet:\$ANDROID_HOME/platform-tools:\$PATH"

MSG
