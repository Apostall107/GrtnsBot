#!/usr/bin/env bash
# Update the bot to a new build. Data (/opt/greatnessbot/data) and token are never touched.
# Run as root from the uploaded NEW deploy-linux folder:   bash update.sh
set -euo pipefail
cd "$(dirname "$0")"

APP=/opt/greatnessbot
SERVICE=greatnessbot

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root:  su -   (or: sudo bash update.sh)"
    exit 1
fi
if [ ! -f app/LfgBot ]; then
    echo "app/LfgBot not found. Upload the whole new deploy-linux folder."
    exit 1
fi

systemctl stop $SERVICE
cp -r app/. "$APP"/
chmod +x "$APP/LfgBot"
chown -R greatnessbot:greatnessbot "$APP"
cp greatnessbot.service /etc/systemd/system/$SERVICE.service
systemctl daemon-reload
systemctl start $SERVICE

sleep 8
journalctl -u $SERVICE -n 30 --no-pager
