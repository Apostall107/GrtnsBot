#!/usr/bin/env bash
# Build Greatness Bot from the git repository ON THE SERVER and install/update the service.
# Run from anywhere as the user who owns the repo (needs sudo):
#     bash /opt/GrtnsBot/deploy/linux/server-build.sh
# Quick build without precompilation (about a minute; the bot starts a bit slower after a restart):
#     R2R=false bash /opt/GrtnsBot/deploy/linux/server-build.sh
# Data (/opt/greatnessbot/data) and token (/etc/greatnessbot.env) are never overwritten.
set -euo pipefail

cd "$(dirname "$0")/../.."
REPO="$(pwd)"
OUT="$REPO/deploy-linux"

case "$(uname -m)" in
    x86_64)  RID=linux-x64 ;;
    aarch64) RID=linux-arm64 ;;
    *) echo "Unsupported CPU: $(uname -m)"; exit 1 ;;
esac

if ! command -v dotnet >/dev/null 2>&1; then
    echo ".NET SDK not found. Install it first (see DEPLOY-DEBIAN.md, section 'Git')."
    exit 1
fi

echo "=== git pull ==="
git pull --ff-only

R2R="${R2R:-true}"
echo "=== Build for $RID (ReadyToRun: $R2R) ==="
rm -rf "$OUT"
# No compression inside the single file: it was unpacked into memory on every start (slower start, more RAM).
# ReadyToRun: code is precompiled — the bot is fast right after a restart. The first build compiles every library (slow on
# a small VPS); later builds reuse obj/ and recompile only what changed. Don't delete obj/ and don't interrupt it.
dotnet publish -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishReadyToRun="$R2R" \
    -p:DebugType=None \
    -o "$OUT/app"

cp appsettings.json "$OUT/app/"
cp deploy/linux/*.sh deploy/linux/*.service deploy/linux/*.md "$OUT/"
# Data from the repo is used only on the very first install (install.sh keeps existing server data)
if [ -d data ]; then cp -r data "$OUT/data"; fi

# Not `systemctl list-unit-files | grep -q`: with pipefail, grep -q closes the pipe early,
# systemctl gets SIGPIPE and the check wrongly falls through to a first install.
if [ -f /etc/systemd/system/greatnessbot.service ]; then
    echo "=== Update ==="
    sudo bash "$OUT/update.sh"
else
    echo "=== First install ==="
    sudo bash "$OUT/install.sh"
fi
