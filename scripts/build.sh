#!/usr/bin/env bash
# Tests and builds an APK into dist/.
#
#   scripts/build.sh           sts2-dualscreen-<version>.apk, for playing
#   scripts/build.sh dev       ...-dev.apk, debuggable
#   scripts/build.sh debug     ...-debug.apk, dev plus debug logging
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

case "${1:-release}" in
  release) target=apk ;;
  dev) target=dev-apk ;;
  debug) target=debug-apk ;;
  *) echo "usage: $0 [release|dev|debug]" >&2; exit 2 ;;
esac

make test
make "$target"
