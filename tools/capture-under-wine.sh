#!/usr/bin/env bash
# Renders every view of the Windows app on Linux: publishes it for win-x64, runs `Boatdrill.exe --capture`
# under Wine with a virtual display, and leaves the PNGs in artifacts/screens. The images are close to,
# not identical with, Windows: Segoe UI is not free, so Liberation Sans stands in for it.
#
# Needs: the .NET 10 SDK, wine64 (or wine), xvfb-run, python3, and the Liberation fonts.
#   Ubuntu: sudo apt install wine64 xvfb python3-venv fonts-liberation
#
# Usage: tools/capture-under-wine.sh [output-directory]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$(realpath -m "${1:-$root/artifacts/screens}")"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/boatdrill-wine"
export WINEPREFIX="$cache/prefix" WINEARCH=win64 WINEDEBUG=-all
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

wine=$(command -v wine64 || command -v wine || true)
[ -z "$wine" ] && [ -x /usr/lib/wine/wine64 ] && wine=/usr/lib/wine/wine64
[ -n "$wine" ] || { echo "wine64 is not installed." >&2; exit 1; }
command -v xvfb-run > /dev/null || { echo "xvfb-run is not installed." >&2; exit 1; }
liberation=/usr/share/fonts/truetype/liberation
[ -f "$liberation/LiberationSans-Regular.ttf" ] || { echo "The Liberation fonts are not installed." >&2; exit 1; }

mkdir -p "$cache"
if [ ! -d "$WINEPREFIX/drive_c/windows" ]; then
    echo "Creating the Wine prefix in $WINEPREFIX..."
    "$wine" wineboot --init > /dev/null 2>&1 || true
fi

# WPF needs fonts under the names Windows has. Copies of Liberation Sans are renamed to them, once.
fonts="$WINEPREFIX/drive_c/windows/Fonts"
if [ ! -f "$fonts/segoeui.ttf" ]; then
    echo "Making stand-in fonts..."
    [ -x "$cache/venv/bin/python" ] || { python3 -m venv "$cache/venv" && "$cache/venv/bin/pip" install -q fonttools; }
    "$cache/venv/bin/python" - "$fonts" "$liberation" <<'PY'
import sys
from fontTools.ttLib import TTFont
fonts, liberation = sys.argv[1], sys.argv[2]
styles = {"Regular": ("", "Regular"), "Bold": ("bd", "Bold"), "Italic": ("i", "Italic")}
for family, stem in [("Segoe UI", "segoeui"), ("Tahoma", "tahoma"), ("Arial", "arial"), ("Microsoft Sans Serif", "micross")]:
    for style, (suffix, source) in styles.items():
        font = TTFont(f"{liberation}/LiberationSans-{source}.ttf")
        full = family if style == "Regular" else f"{family} {style}"
        for record in font["name"].names:
            record.string = {1: family, 16: family, 2: style, 17: style, 4: full, 6: full.replace(" ", ""), 3: "stand-in:" + full}.get(record.nameID, record.toUnicode())
        font.save(f"{fonts}/{stem}{suffix}.ttf")
PY
    key='HKLM\Software\Microsoft\Windows NT\CurrentVersion\Fonts'
    for entry in "Segoe UI:segoeui" "Segoe UI Bold:segoeuibd" "Segoe UI Italic:segoeuii" "Tahoma:tahoma" "Tahoma Bold:tahomabd" \
                 "Arial:arial" "Arial Bold:arialbd" "Arial Italic:ariali" "Microsoft Sans Serif:micross"; do
        "$wine" reg add "$key" /v "${entry%%:*} (TrueType)" /t REG_SZ /d "${entry##*:}.ttf" /f > /dev/null 2>&1
    done
    "$(dirname "$wine")/wineserver" -w 2>/dev/null || wineserver -w 2>/dev/null || true
fi

echo "Publishing the app for win-x64..."
publish="$cache/app"
dotnet publish "$root/src/Boatdrill/Boatdrill.csproj" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=false -o "$publish" --nologo -v q

echo "Capturing the views to $out..."
rm -rf "$out"; mkdir -p "$out"
windows_out="Z:$(echo "$out" | sed 's|/|\\|g')"
set +e
(cd "$publish" && xvfb-run -a -s "-screen 0 1600x1000x24" "$wine" ./Boatdrill.exe --capture "$windows_out" > "$out/wine.log" 2>&1)
code=$?
set -e

ls "$out"
case $code in
    0) echo "Done." ;;
    2) echo "A binding names a property that does not exist; see $out/binding-messages.txt." >&2 ;;
    *) echo "The capture failed with exit code $code; see $out/capture-error.txt and $out/wine.log." >&2 ;;
esac
exit $code
