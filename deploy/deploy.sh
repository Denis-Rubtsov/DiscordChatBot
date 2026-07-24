#!/usr/bin/env bash
# Обновляет и перезапускает бота на VPS. Запускать на самой VPS (например, из
# автоматизации Shortcuts через SSH-команду), не с Mac.
set -euo pipefail

REPO_DIR="${REPO_DIR:-$HOME/DiscordChatBot}"
OUT_DIR="$REPO_DIR/out"
SERVICE_NAME="discordchatbot"

cd "$REPO_DIR"
git pull --ff-only
dotnet publish DiscordChatBot/DiscordChatBot.csproj -c Release -o "$OUT_DIR"
systemctl restart "$SERVICE_NAME"
systemctl status "$SERVICE_NAME" --no-pager -l
