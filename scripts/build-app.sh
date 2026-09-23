#!/bin/bash
# Builds ScreenPlus and wraps it in a signed .app bundle (required for Screen Recording permission).
#
# Signing with a stable identity keeps the Screen Recording permission across rebuilds.
# Override with: SIGN_IDENTITY="-" ./scripts/build-app.sh   (ad-hoc; macOS will re-ask for permission after each build)
set -euo pipefail

cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-release}"
APP="build/ScreenPlus.app"

swift build -c "$CONFIG"
./scripts/build-icon.sh

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp ".build/$CONFIG/ScreenPlus" "$APP/Contents/MacOS/ScreenPlus"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/"

if [ -n "${SIGN_IDENTITY:-}" ]; then
    codesign --force --sign "$SIGN_IDENTITY" "$APP"
else
    # Pick the first Apple Development certificate that isn't revoked. macOS reports apps
    # signed with a revoked certificate as malware and moves them to the Trash.
    SIGN_IDENTITY="-"
    for id in $(security find-identity -v -p codesigning | awk '/Apple Development/ {print $2}'); do
        codesign --force --sign "$id" "$APP" 2>/dev/null || continue
        assessment=$(spctl --assess -vv --type execute "$APP" 2>&1 || true)
        if [[ "$assessment" != *REVOKED* ]]; then
            SIGN_IDENTITY="$id"
            break
        fi
    done
    [ "$SIGN_IDENTITY" = "-" ] && codesign --force --sign - "$APP"
fi
echo "Built $APP (signed with: $SIGN_IDENTITY)"

if [ "${1:-}" = "--run" ]; then
    open "$APP"
fi
