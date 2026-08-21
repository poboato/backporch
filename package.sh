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

rm -rf "$OUT"
mkdir -p "$STAGE"

"$DOTNET" publish Jellyfin.Plugin.Backporch/Jellyfin.Plugin.Backporch.csproj -c Release -o "$OUT/publish"

# Certes depends on Newtonsoft.Json, so it ships too. Jellyfin happens to carry its
# own copy today, but relying on that would make the plugin break on any server
# release that drops or downgrades it.
cp "$OUT/publish/Jellyfin.Plugin.Backporch.dll" \
   "$OUT/publish/Certes.dll" \
   "$OUT/publish/BouncyCastle.Crypto.dll" \
   "$OUT/publish/Newtonsoft.Json.dll" \
   "$STAGE/"

(cd "$STAGE" && zip -q "../${NAME}_${VERSION}.zip" ./*)

md5sum "$OUT/${NAME}_${VERSION}.zip"
echo "Wrote $OUT/${NAME}_${VERSION}.zip"
