#!/usr/bin/env bash
# Publishes a GitHub release from this machine, with the VFX-pool compat pack
# CI can't build (it has no sts2.dll):
#
#   1. sets sts2ds.version to VERSION and commits it, if it differs
#   2. tests and builds dist/sts2-dualscreen-<version>.apk (scripts/build.sh)
#   3. checks it is signed like the latest release, so it installs over it
#   4. asks, then pushes main and tag v<version> and uploads the APK
#
#   scripts/release.sh [VERSION] [NOTES_FILE]
#
# VERSION defaults to the current sts2ds.version. Without NOTES_FILE, GitHub
# writes the notes from the commits since the last release.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

PROPS=launcher/gradle.properties
BRANCH=main
JAVA_HOME="${JAVA_HOME:-$(brew --prefix openjdk@21)/libexec/openjdk.jdk/Contents/Home}"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
export JAVA_HOME ANDROID_HOME

die() { echo "release: $*" >&2; exit 1; }
current_version() { sed -n 's/^sts2ds\.version=//p' "$PROPS"; }

# Releases come from a clean main, by a signed-in gh.
[[ "$(git branch --show-current)" == "$BRANCH" ]] || die "not on $BRANCH"
[[ -z "$(git status --porcelain)" ]] || die "uncommitted changes"
gh auth status >/dev/null 2>&1 || die "gh is not signed in (gh auth login)"

version="${1:-$(current_version)}"
notes="${2:-}"
tag="v$version"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "bad version: $version (want e.g. 0.1.2)"
[[ -z "$notes" || -f "$notes" ]] || die "no notes file: $notes"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null || git ls-remote --exit-code --tags origin "$tag" >/dev/null; then
  die "tag $tag already exists"
fi

# Version bump, committed so the tag points at it.
if [[ "$version" != "$(current_version)" ]]; then
  sed -i '' "s/^sts2ds\.version=.*/sts2ds.version=$version/" "$PROPS"
  git commit -q -m "Bump version to $version" -- "$PROPS"
fi

scripts/build.sh release
apk="dist/sts2-dualscreen-$version.apk"

# A different signing key would make Android refuse the update.
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
apksigner="$(ls -d "$ANDROID_HOME"/build-tools/* | sort -V | tail -1)/apksigner"
cert() { "$apksigner" verify --print-certs "$1" | sed -n 's/^Signer #1 certificate SHA-256 digest: //p'; }
mkdir "$work/latest" "$work/out"
if gh release download --pattern '*.apk' --dir "$work/latest" 2>/dev/null; then
  latest=("$work"/latest/*.apk)
  [[ "$(cert "$apk")" == "$(cert "${latest[0]}")" ]] || die "$apk is signed with another key than the latest release"
fi

answer=""
read -r -p "Publish $tag from $(git rev-parse --short HEAD) to GitHub? [y/N] " answer || true
[[ "$answer" == y ]] || die "cancelled; nothing pushed"

git push origin "$BRANCH"
git tag "$tag"
git push origin "$tag"

# Named like the earlier releases' asset.
asset="$work/out/sts2-dualscreen-$version-release.apk"
cp "$apk" "$asset"
if [[ -n "$notes" ]]; then
  gh release create "$tag" "$asset" --title "$tag" --verify-tag --notes-file "$notes"
else
  gh release create "$tag" "$asset" --title "$tag" --verify-tag --generate-notes
fi
