#!/usr/bin/env bash
# Rebuild one target of the compat pack from source, newer than the release.
#
# The runtime (make-runtime.sh) ships the compat pack of a Sts2MobileLauncher
# release. Upstream fixes land in ModinMobileSTS/sts2-android-compat first;
# this builds a pinned commit of it with upstream's own matrix script, for the
# game version in COMPAT_REF, and swaps that one target into the bundled
# pack. Every other target stays as released.
#
#   COMPAT_REF     dir holding the user's own sts2.dll (e.g. adb pull of the
#                  device depot's data_sts2_windows_x86_64). GodotSharp.dll and
#                  0Harmony.dll are taken from it if present, else from the
#                  runtime's dotnet_bcl (what the game loads on Android).
#   COMPAT_COMMIT  sts2-android-compat commit (default below)
#   COMPAT_TARGET  target id to build (default: the one whose sts2.dll sha matches)
#
# Nothing built here is committed: the assets are gitignored.
# make runtime restores the released pack: run this again after it.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
COMPAT_REPO="https://github.com/ModinMobileSTS/sts2-android-compat.git"
COMMIT="${COMPAT_COMMIT:-450082d894167267408e75e014018f684253948d}"
CACHE="$ROOT/.cache/runtime/compat"
SRC="$CACHE/src"
ASSETS="$ROOT/launcher/app/src/main/assets"
PACK="$ASSETS/compat_packs/sts2-android-compat.zip"
BCL="$ASSETS/dotnet_bcl"
DOTNET="${DOTNET:-$(command -v dotnet 2>/dev/null || echo "$HOME/.dotnet/dotnet")}"

die() { echo "make-compat: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null || die "missing '$1'"; }
need git; need python3; need zip; need sha256sum
[[ -x "$DOTNET" ]] || die "no dotnet at $DOTNET"
[[ -n "${COMPAT_REF:-}" ]] || die "set COMPAT_REF to a dir holding the game's sts2.dll"
[[ -f "$COMPAT_REF/sts2.dll" ]] || die "no sts2.dll in $COMPAT_REF"
[[ -f "$PACK" ]] || die "no bundled pack at $PACK (run make runtime)"

# 1. Source at the pinned commit ----------------------------------------------
if [[ ! -d "$SRC/.git" ]]; then
  git clone -q "$COMPAT_REPO" "$SRC"
fi
if ! git -C "$SRC" cat-file -e "$COMMIT^{commit}" 2>/dev/null; then
  git -C "$SRC" fetch -q origin
fi
git -C "$SRC" checkout -q --force "$COMMIT"
git -C "$SRC" clean -qfdx

# 2. Target: the bundled manifest's entry for this sts2.dll --------------------
DLL_SHA="$(sha256sum "$COMPAT_REF/sts2.dll" | cut -d' ' -f1)"
read -r TARGET FLAVOR < <(python3 - "$PACK" "$DLL_SHA" "${COMPAT_TARGET:-}" <<'PY'
import json, sys, zipfile
pack, sha, wanted = sys.argv[1:]
with zipfile.ZipFile(pack) as z:
    name = next(n for n in z.namelist() if n.endswith("/compat_manifest.json"))
    targets = json.loads(z.read(name))["targets"]
def shas(t):
    v = t.get("sts2_dll_sha256") or []
    return [v] if isinstance(v, str) else v
for t in targets:
    if t["target_id"] == wanted or (not wanted and sha in shas(t)):
        print(t["target_id"], t["reference_flavor"])
        break
else:
    sys.exit(f"no target for sts2.dll {sha[:12]}; set COMPAT_TARGET")
PY
)
echo "Building compat $TARGET ($FLAVOR) at ${COMMIT:0:12} for sts2.dll ${DLL_SHA:0:12}"

# 3. Reference dir: the game's sts2.dll, the runtime's Godot/Harmony -----------
REF="$CACHE/ref-$TARGET"
rm -rf "$REF" && mkdir -p "$REF"
cp "$COMPAT_REF/sts2.dll" "$REF/"
for dll in GodotSharp.dll 0Harmony.dll; do
  if [[ -f "$COMPAT_REF/$dll" ]]; then cp "$COMPAT_REF/$dll" "$REF/"; else cp "$BCL/$dll" "$REF/"; fi
done

# 4. Upstream's matrix build, this target only ----------------------------------
# Its flavor → reference dir lookup reads STS2_ORIGINAL_V<digits>_REFERENCE_DIR
# (v0.111.0 → V1110); the runtime flavor reads STS2_RUNTIME_REFERENCE_DIR.
digits="$(echo "${FLAVOR#original-v}" | tr -d '.')"
digits="${digits#0}"
case "$FLAVOR" in
  original) export STS2_ORIGINAL_V103_REFERENCE_DIR="$REF" ;;
  original-v*) export "STS2_ORIGINAL_V${digits}_REFERENCE_DIR=$REF" ;;
  *) export STS2_RUNTIME_REFERENCE_DIR="$REF" ;;
esac
OUT="$CACHE/out"
DOTNET_BIN="$DOTNET" "$SRC/tools/build-compat-matrix.sh" --target "$TARGET" --out "$OUT"
BUILT="$OUT/sts2-android-compat"

# 5. Swap the target into the bundled pack --------------------------------------
python3 - "$PACK" "$BUILT" "$TARGET" "$COMMIT" <<'PY'
import hashlib, json, os, sys, zipfile
pack, built, target, commit = sys.argv[1:]
prefix = "sts2-android-compat/"
fresh = json.load(open(os.path.join(built, "compat_manifest.json"), encoding="utf-8"))
entry = next(t for t in fresh["targets"] if t["target_id"] == target)
entry["build_info"] = dict(fresh["build_info"], commit=commit[:12])
files = {f"{prefix}{entry['artifacts'][k]}": os.path.join(built, entry["artifacts"][k]) for k in ("dll", "overlay_pck")}

with zipfile.ZipFile(pack) as src:
    infos = src.infolist()
    data = {i.filename: src.read(i.filename) for i in infos}
manifest_name = prefix + "compat_manifest.json"
manifest = json.loads(data[manifest_name])
manifest["targets"] = [entry if t["target_id"] == target else t for t in manifest["targets"]]
data[manifest_name] = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode()
for name, path in files.items():
    data[name] = open(path, "rb").read()

# SHA256SUMS lists the manifest and every variant file, as upstream writes it.
sums = sorted(n for n in data if n.startswith(prefix + "variants/") and not n.endswith("/")) + [manifest_name]
data[prefix + "SHA256SUMS"] = "".join(
    f"{hashlib.sha256(data[n]).hexdigest()}  {n[len(prefix):]}\n" for n in sorted(sums)).encode()

tmp = pack + ".tmp"
with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as out:
    for info in infos:
        out.writestr(info, data[info.filename])
os.replace(tmp, pack)
print(f"Swapped {target} into {pack}")
PY

# 6. Record it ----------------------------------------------------------------
python3 - "$ASSETS/runtime_manifest.json" "$TARGET" "$COMMIT" <<'PY'
import json, sys
path, target, commit = sys.argv[1:]
data = json.load(open(path))
rebuilt = data.setdefault("compat_rebuilt", {})
rebuilt[target] = commit[:12]
json.dump(data, open(path, "w"), indent=2)
open(path, "a").write("\n")
PY
