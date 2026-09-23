#!/bin/bash
# Packages ScreenPlus into a distributable .dmg.
#
#   ./scripts/package-dmg.sh              Signed with Developer ID, notarized and stapled.
#                                         This is the version other people can open without warnings.
#   ./scripts/package-dmg.sh --unsigned   Free: ad-hoc signed, no Apple Developer Program needed.
#                                         Recipients approve it once in System Settings → Privacy &
#                                         Security → "Open Anyway" (instructions are included in the DMG).
#
# One-time setup for the signed build (requires a paid Apple Developer Program membership):
#   1. Xcode → Settings → Accounts → Manage Certificates → + → "Developer ID Application"
#   2. Create an app-specific password at https://account.apple.com (Sign-In and Security)
#   3. xcrun notarytool store-credentials ScreenPlus --apple-id <you@example.com> --team-id <TEAMID>
#
# Overrides: DEVELOPER_ID="Developer ID Application: Name (TEAMID)"  NOTARY_PROFILE=ScreenPlus
set -euo pipefail
cd "$(dirname "$0")/.."

MODE="${1:-signed}"
VERSION=$(plutil -extract CFBundleShortVersionString raw Resources/Info.plist)
DIST="build/dist"
APP="$DIST/ScreenPlus.app"
DMG="build/ScreenPlus-$VERSION.dmg"
NOTARY_PROFILE="${NOTARY_PROFILE:-ScreenPlus}"

if [ "$MODE" != "--unsigned" ]; then
    DEVELOPER_ID="${DEVELOPER_ID:-$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/ {print $2; exit}')}"
    if [ -z "$DEVELOPER_ID" ]; then
        echo "error: no 'Developer ID Application' certificate found in your keychain." >&2
        echo "       See the setup steps at the top of $0, or run with --unsigned to test packaging." >&2
        exit 1
    fi
fi

echo "==> Building universal binary (Apple silicon + Intel)"
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/ScreenPlus"

echo "==> Assembling $APP"
./scripts/build-icon.sh
rm -rf "$DIST"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/ScreenPlus"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/"

if [ "$MODE" = "--unsigned" ]; then
    codesign --force --sign - "$APP"
else
    echo "==> Signing with: $DEVELOPER_ID (hardened runtime)"
    codesign --force --options runtime --timestamp --sign "$DEVELOPER_ID" "$APP"
    codesign --verify --strict --verbose=2 "$APP"
fi

echo "==> Creating $DMG"
STAGING=$(mktemp -d)
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"  # drag-to-install
if [ "$MODE" = "--unsigned" ]; then
    cat > "$STAGING/How to open ScreenPlus.txt" <<'TXT'
How to install ScreenPlus
=========================

1. Drag ScreenPlus into the Applications folder.
2. Open ScreenPlus. macOS will say it "could not verify ScreenPlus is free of malware",
   because the app isn't registered with Apple. Click "Done".
3. Open System Settings → Privacy & Security, scroll down to
   "ScreenPlus was blocked to protect your Mac", and click "Open Anyway".
   Enter your Mac password to confirm.

You only need to do this once. After that ScreenPlus opens normally.

Terminal alternative (does steps 2-3 in one go, after step 1):
    xattr -dr com.apple.quarantine /Applications/ScreenPlus.app

On first recording, ScreenPlus asks for Screen Recording permission (required) and
Accessibility permission (optional, only for keyboard sounds). Relaunch after granting.
TXT
fi
rm -f "$DMG"
hdiutil create -volname "ScreenPlus" -srcfolder "$STAGING" -ov -format UDZO -quiet "$DMG"
rm -rf "$STAGING"

if [ "$MODE" = "--unsigned" ]; then
    echo "Done: $DMG (not notarized — recipients approve it once via \"Open Anyway\"; see the .txt inside)"
    exit 0
fi

codesign --force --timestamp --sign "$DEVELOPER_ID" "$DMG"

echo "==> Notarizing (usually takes a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$DMG"

echo "==> Gatekeeper check"
spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG"
echo "Done: $DMG is signed, notarized and ready to share."
