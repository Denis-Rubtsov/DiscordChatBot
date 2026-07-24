#!/usr/bin/env bash
# Обновляет и перезапускает бота на VPS. Запускать на самой VPS (например, из
# автоматизации Shortcuts через SSH-команду), не с Mac — тут нет SSH-ключа к VPS.
set -euo pipefail

REPO_DIR="${REPO_DIR:-$HOME/DiscordChatBot}"
PUBLISH_DIR="$REPO_DIR/DiscordChatBot/bin/publish"
SERVICE_NAME="discordchatbot"

cd "$REPO_DIR"
git pull --ff-only
dotnet publish DiscordChatBot/DiscordChatBot.csproj -c Release -o "$PUBLISH_DIR"
sudo systemctl restart "$SERVICE_NAME"
sudo systemctl status "$SERVICE_NAME" --no-pager -l
