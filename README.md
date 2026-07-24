# DiscordChatBot

Discord-бот с личностью милой кошкодевочки (Мурка 🐱), отвечающий через OpenAI (`gpt-4o-mini` по умолчанию).

Бот отвечает:
- на любые сообщения в личных сообщениях (DM);
- на упоминания (`@Мурка ...`) в текстовых каналах сервера.

Контекст диалога хранится в памяти отдельно по каждому каналу (последние 10 обменов репликами), сбрасывается при перезапуске бота.

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
- `OpenAI__Model` — модель OpenAI (по умолчанию `gpt-4o-mini`).
- `SystemPromptFile` — путь к файлу с системным промптом личности; перечитывается при каждом ответе, так что правки применяются без перезапуска.

## Запуск

Системный `dotnet` на этой машине — версии 7.x, нужен .NET 8 SDK:

```bash
cd DiscordChatBot
~/.dotnet8/dotnet run
```
