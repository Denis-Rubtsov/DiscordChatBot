#!/usr/bin/env bash
# Обновляет и перезапускает бота на VPS. Запускать на самой VPS (например, из
# автоматизации Shortcuts через SSH-команду), не с Mac.
set -euo pipefail

REPO_DIR="${REPO_DIR:-$HOME/DiscordChatBot}"
OUT_DIR="$REPO_DIR/out"
SERVICE_NAME="discordchatbot"

cd "$REPO_DIR"
git pull --ff-only

# dotnet publish перезаписывает appsettings.json из репозитория (там только
# плейсхолдеры) каждый раз, когда его содержимое меняется в git — без этого
# бэкапа реальные секреты слетали бы на каждом деплое, добавляющем новый ключ
# конфига.
SECRETS_BACKUP=""
if [ -f "$OUT_DIR/appsettings.json" ]; then
    SECRETS_BACKUP="$(mktemp)"
    cp "$OUT_DIR/appsettings.json" "$SECRETS_BACKUP"
fi

dotnet publish DiscordChatBot/DiscordChatBot.csproj -c Release -o "$OUT_DIR"

if [ -n "$SECRETS_BACKUP" ]; then
    cp "$SECRETS_BACKUP" "$OUT_DIR/appsettings.json"
    rm -f "$SECRETS_BACKUP"
fi

# libdave.so не публикуется MSBuild'ом (это не .NET-сборка) — докачиваем, если
# его вдруг нет в out/ (например, самый первый деплой на новую машину).
if [ ! -f "$OUT_DIR/libdave.so" ]; then
    curl -sSL -o /tmp/libdave.zip https://github.com/discord/libdave/releases/download/v1.1.1/cpp/libdave-Linux-X64-boringssl.zip
    unzip -o -j /tmp/libdave.zip 'lib/libdave.so' -d "$OUT_DIR/"
fi

systemctl restart "$SERVICE_NAME"
systemctl status "$SERVICE_NAME" --no-pager -l
