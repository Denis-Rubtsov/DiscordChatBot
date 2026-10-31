#!/usr/bin/env bash
# Создаёт (или обновляет) Podman-секреты discordchatbot из appsettings.json, например из бекапа VPS:
#   bash deploy/import-secrets.sh ~/restore/root/<бот>/out/appsettings.json
# Значения нигде не печатаются. После импорта файл с токенами можно удалить.
set -euo pipefail
cfg=${1:?usage: $0 <appsettings.json>}

get() {  # get <ключ через двоеточие>: значение из JSON, ошибка если пусто или заглушка
  python3 -I -c '
import json, sys
v = json.load(open(sys.argv[1]))
for p in sys.argv[2].split(":"):
    v = v[p]
v = str(v)
if not v.strip() or "PLACEHOLDER" in v.upper():
    sys.exit(sys.argv[2] + ": пусто или заглушка")
sys.stdout.write(v)' "$cfg" "$1"
}

while read -r secret key; do
  value=$(get "$key")
  printf %s "$value" | podman secret create --replace "$secret" - >/dev/null
  echo "ok: $secret"
done <<'LIST'
discordchatbot-discord-token Discord:Token
discordchatbot-openai-apikey OpenAI:ApiKey
LIST
