#!/usr/bin/env bash
# First-time installation of Greatness Bot on Debian/Ubuntu.
# Run as root from the uploaded deploy-linux folder:   bash install.sh
set -euo pipefail
cd "$(dirname "$0")"

APP=/opt/greatnessbot
SERVICE=greatnessbot
ENV_FILE=/etc/greatnessbot.env

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root:  su -   (or: sudo bash install.sh)"
    exit 1
fi
if [ ! -f app/LfgBot ]; then
    echo "app/LfgBot not found. Upload the whole deploy-linux folder built by publish-linux.bat."
    exit 1
fi

# Install the first package from the list that exists in this Debian/Ubuntu version
install_first() {
    for pkg in "$@"; do
        if apt-cache show "$pkg" >/dev/null 2>&1; then
            apt-get install -y "$pkg" && return 0
        fi
    done
    echo "WARNING: none of these packages found: $*"
}

echo "=== 1/5 System libraries for .NET ==="
apt-get update
apt-get install -y ca-certificates tzdata libstdc++6 zlib1g
install_first libicu76 libicu74 libicu72 libicu70 libicu67 libicu-dev
install_first libssl3t64 libssl3 libssl1.1

echo "=== 2/5 Service user and files ==="
id -u greatnessbot >/dev/null 2>&1 || \
    useradd --system --home-dir "$APP" --shell /usr/sbin/nologin greatnessbot
mkdir -p "$APP"
cp -r app/. "$APP"/
if [ -d "$APP/data" ]; then
    echo "Keeping existing $APP/data"
elif [ -d data ]; then
    cp -r data "$APP/data"
else
    mkdir -p "$APP/data"
fi
chmod +x "$APP/LfgBot"
chown -R greatnessbot:greatnessbot "$APP"

echo "=== 3/5 Bot token ==="
if [ -f "$ENV_FILE" ]; then
    echo "Keeping existing $ENV_FILE"
else
    read -rsp "Paste the Discord bot token (input is hidden) and press Enter: " TOKEN
    echo
    if [ -z "$TOKEN" ]; then
        echo "Empty token. Run install.sh again."
        exit 1
    fi
    printf 'Discord__Token=%s\n' "$TOKEN" > "$ENV_FILE"
    chmod 600 "$ENV_FILE"
fi

echo "=== 4/5 systemd service ==="
cp greatnessbot.service /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
systemctl enable $SERVICE
systemctl restart $SERVICE

echo "=== 5/5 Check ==="
sleep 8
systemctl --no-pager --lines=0 status $SERVICE || true
echo
journalctl -u $SERVICE -n 20 --no-pager || true
echo
echo "Done. Live log: journalctl -u $SERVICE -f   (Ctrl+C to exit, the bot keeps running)"
