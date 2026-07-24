# DiscordChatBot

Discord-бот с личностью милой кошкодевочки (Мурка 🐱), отвечающий через OpenAI (`gpt-5.4` по умолчанию).

Бот отвечает:
- на любые сообщения в личных сообщениях (DM);
- на упоминания (`@Мурка ...`) в текстовых каналах сервера — а после первого упоминания в канале начинает отвечать на все сообщения в нём, без повторных упоминаний, пока бот не перезапустят.

Контекст диалога хранится в памяти отдельно по каждому каналу (последние 50 обменов репликами), сбрасывается при перезапуске бота.

**Память об отношениях.** Отдельно от истории диалога бот ведёт долговременную заметку о каждом участнике сервера (по его Discord ID), которая переживает перезапуски — хранится в JSON-файле (`RelationshipsFile`, по умолчанию `relationships.json`, создаётся автоматически рядом с бинарником). После каждого ответа модель сама обновляет эту заметку (кто ей этот человек, как она к нему относится, что помнит важного) и учитывает её в следующих ответах этому же человеку в любом канале.

## Настройка

1. Создай приложение в [Discord Developer Portal](https://discord.com/developers/applications), добавь Bot.
2. В разделе Bot включи **Message Content Intent** (Privileged Gateway Intents) — без него бот не увидит текст сообщений.
3. Скопируй Bot Token.
4. Пригласи бота на сервер через OAuth2 URL Generator со scope `bot` и правами как минимум "Send Messages" и "Read Message History".
5. Получи OpenAI API-ключ на platform.openai.com.

Задай реальные значения через переменные окружения (не редактируй `appsettings.json` — там только плейсхолдеры, чтобы не закоммитить секреты):

```bash
export Discord__Token="..."
export OpenAI__ApiKey="..."
```

Опционально:
- `OpenAI__Model` — модель OpenAI (по умолчанию `gpt-5.4`).
- `SystemPromptFile` — путь к файлу с системным промптом личности; перечитывается при каждом ответе, так что правки применяются без перезапуска.

## Деплой на VPS (systemd)

Файлы в `deploy/` рассчитаны на VPS с уже установленным .NET 8 SDK/Runtime (тот же сервер и та же схема, что и у бота Vlk/WolfsQuotes: репозиторий в `/root`, публикация в поддиректорию `out/`, реальные секреты — прямо в `out/appsettings.json`, который никогда не коммитится).

**Первоначальная настройка (один раз, на самой VPS, от root):**

```bash
git clone https://github.com/Denis-Rubtsov/DiscordChatBot.git /root/DiscordChatBot
cd /root/DiscordChatBot
dotnet publish DiscordChatBot/DiscordChatBot.csproj -c Release -o out

# заполни реальными значениями (этот файл не в git — редактируется только на сервере);
# RelationshipsFile стоит указать вне out/, например /data/relationships.json,
# чтобы не терялось при следующем dotnet publish
$EDITOR out/appsettings.json

cp deploy/discordchatbot.service /etc/systemd/system/discordchatbot.service
systemctl daemon-reload
systemctl enable --now discordchatbot
```

**Обновление после пуша в main** — на VPS:

```bash
cd /root/DiscordChatBot && bash deploy/deploy.sh
```

(или удалённо: `ssh <host> 'cd DiscordChatBot && bash deploy/deploy.sh'`).

Логи: `journalctl -u discordchatbot -f`.
