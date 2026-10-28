#!/usr/bin/env bash
# Builds the plugin and produces a zip installable by hand or via a plugin
# repository. Only the plugin assembly and true third-party dependencies are
# shipped — never Jellyfin's own assemblies, which would collide with the
# server's copies at load time.
set -euo pipefail

cd "$(dirname "$0")"

DOTNET="${DOTNET:-dotnet}"
VERSION="0.1.0.0"
NAME="backporch"
OUT="dist"
STAGE="$OUT/stage"

# JELLYFIN_ABI picks the server line: 12 (default) or 10.11. The zips are not
# interchangeable -- Jellyfin 12 runs on .NET 10 and needs the 12 build.
JELLYFIN_ABI="${JELLYFIN_ABI:-12}"
case "$JELLYFIN_ABI" in
    12)    SUFFIX="" ;;
    10.11) SUFFIX="_jellyfin-10.11" ;;
    *)     echo "JELLYFIN_ABI must be 12 or 10.11, not '$JELLYFIN_ABI'" >&2; exit 2 ;;
esac

rm -rf "$OUT/publish" "$STAGE"
mkdir -p "$STAGE"

"$DOTNET" publish Jellyfin.Plugin.Backporch/Jellyfin.Plugin.Backporch.csproj -c Release \
    -p:JellyfinAbi="$JELLYFIN_ABI" -o "$OUT/publish"

# Certes depends on Newtonsoft.Json, so it ships too. Jellyfin happens to carry its
# own copy today, but relying on that would make the plugin break on any server
# release that drops or downgrades it.
cp "$OUT/publish/Jellyfin.Plugin.Backporch.dll" \
   "$OUT/publish/Certes.dll" \
   "$OUT/publish/BouncyCastle.Crypto.dll" \
   "$OUT/publish/Newtonsoft.Json.dll" \
   "$STAGE/"

ZIP="${NAME}_${VERSION}${SUFFIX}.zip"
rm -f "$OUT/$ZIP"
(cd "$STAGE" && zip -q "../$ZIP" ./*)

md5sum "$OUT/$ZIP"
echo "Wrote $OUT/$ZIP (Jellyfin $JELLYFIN_ABI)"
