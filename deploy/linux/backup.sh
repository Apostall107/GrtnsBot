#!/usr/bin/env bash
# Backup of bot data: /root/greatnessbot-backups/data-YYYY-MM-DD_HHMM.tar.gz (keeps the last 30)
# Run as root:  bash backup.sh
# Daily at 04:00 via cron:  crontab -e  ->  0 4 * * * bash /root/deploy-linux/backup.sh
set -euo pipefail

APP=/opt/greatnessbot
DEST=/root/greatnessbot-backups

mkdir -p "$DEST"
FILE="$DEST/data-$(date +%F_%H%M).tar.gz"
tar -czf "$FILE" -C "$APP" data
ls -1t "$DEST"/data-*.tar.gz | tail -n +31 | xargs -r rm -f
echo "Backup: $FILE"
