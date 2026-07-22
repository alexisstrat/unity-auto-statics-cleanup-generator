#!/usr/bin/env bash
# Builds the setup .unitypackage from the committed UnityPackage/Assets
# tree (attributes, DelegateAutoCleanup, editor registrar). The analyzer DLL
# is deliberately NOT included — it ships as its own release asset so it can
# be attestation-verified before being dropped into a project.
#
# A .unitypackage is a gzipped tar with one directory per asset, named by the
# asset's GUID (taken from its committed .meta), containing:
#   asset      - the file's content (absent for folder assets)
#   asset.meta - the Unity .meta file
#   pathname   - the project-relative path the asset unpacks to
#
# Usage: build-unitypackage.sh <output.unitypackage>

set -euo pipefail

OUT="$1"
ROOT="$(cd "$(dirname "$0")" && pwd)"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

find "$ROOT/Assets" -name '*.meta' | LC_ALL=C sort | while read -r meta; do
  asset="${meta%.meta}"
  rel="${asset#"$ROOT"/}"
  guid="$(sed -n 's/^guid: \([0-9a-f]\{32\}\)$/\1/p' "$meta")"
  [ -n "$guid" ] || { echo "error: no guid in $meta" >&2; exit 1; }
  mkdir "$STAGE/$guid"
  cp "$meta" "$STAGE/$guid/asset.meta"
  printf '%s\n' "$rel" > "$STAGE/$guid/pathname"
  [ -d "$asset" ] || cp "$asset" "$STAGE/$guid/asset"
done

mkdir -p "$(dirname "$OUT")"
OUT_ABS="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")"

# COPYFILE_DISABLE keeps macOS bsdtar from adding ._* AppleDouble entries.
(cd "$STAGE" && COPYFILE_DISABLE=1 tar --format ustar -czf "$OUT_ABS" -- */)
echo "wrote $OUT_ABS"
