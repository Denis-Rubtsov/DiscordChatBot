using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using OpenAI.Images;

record CatGirlReply(string Text, string? ImagePrompt);

class CatGirlAiService
{
    private const string DefaultSystemPrompt =
        "Ты — Мурка, милая и игривая кошкодевочка-нейронка, общающаяся с людьми в Discord. " +
        "Ты дружелюбная, ласковая, слегка кокетливая и очень любопытная. Обожаешь мурлыкать и вставлять " +
        "кошачьи словечки («мяу~», «мур-мур», «нья~», «*трётся о плечо*», «*виляет хвостиком*»), но не в каждом " +
        "предложении — в меру, чтобы не надоедать. Отвечай в непринуждённом, тёплом тоне, как заботливая кошечка, " +
        "которая обожает своих людей. Используй эмодзи умеренно (🐾, 🐱, ❤️). Держи ответы короткими и живыми — " +
        "1-4 предложения, без занудства и длинных простыней текста. Ты можешь быть игривой и немного дерзкой, " +
        "но всегда доброй. Если тебя спрашивают о чём-то по существу — отвечай полезно и по делу, но не теряй " +
        "кошачий характер речи. Пиши на русском языке, если собеседник не пишет на другом. " +
        "У тебя есть личная память об отношениях с каждым собеседником (relationship_note) — используй её, чтобы " +
        "помнить, кто он тебе, и вести себя соответственно (теплее с друзьями, настороженнее с грубиянами и т.д.), " +
        "и после каждого ответа обновляй её кратким (до 300 символов) выводом на основе всего разговора.";

    private const int MaxHistoryMessages = 100;

    private static readonly ChatResponseFormat ReplyFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "catgirl_reply",
        jsonSchema: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "reply": {
                    "type": "string",
                    "description": "Ответ Мурки собеседнику, который будет отправлен в Discord."
                },
                "relationship_note": {
                    "type": "string",
                    "description": "Обновлённая краткая (до 300 символов) личная заметка Мурки об этом собеседнике: кто он ей, как она к нему относится, что помнит важного."
                },
                "image_prompt": {
                    "type": ["string", "null"],
                    "description": "Если собеседник прямо просит нарисовать/сгенерировать картинку — подробный промпт на английском для генератора изображений (в стиле, который просят). Во всех остальных случаях null."
                }
            },
            "required": ["reply", "relationship_note", "image_prompt"],
            "additionalProperties": false
        }
        """),
        jsonSchemaFormatDescription: "Ответ кошкодевочки вместе с обновлённой заметкой об отношениях с собеседником",
        jsonSchemaIsStrict: true);

    private static readonly ChatResponseFormat ModerationFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "rule_check",
        jsonSchema: BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "violates": {
                    "type": "boolean",
                    "description": "Явно ли сообщение нарушает правила сервера."
                },
                "warning": {
                    "type": ["string", "null"],
                    "description": "Если нарушает — короткое (1-2 предложения) предупреждение от Мурки нарушителю с указанием пункта правил, иначе null."
                }
            },
            "required": ["violates", "warning"],
            "additionalProperties": false
        }
        """),
        jsonSchemaFormatDescription: "Вердикт Мурки как глашатая правил сервера",
        jsonSchemaIsStrict: true);

    private readonly ChatClient _client;
    private readonly ImageClient _imageClient;
    private readonly RelationshipStore _relationships;
    private readonly BotStateStore _state;
    private readonly string? _promptFile;
    private readonly ILogger<CatGirlAiService> _logger;

    public CatGirlAiService(string apiKey, string model, string imageModel, string? promptFile,
        RelationshipStore relationships, BotStateStore state, ILogger<CatGirlAiService> logger)
    {
        _client = new ChatClient(model, apiKey);
        _imageClient = new ImageClient(imageModel, apiKey);
        _relationships = relationships;
        _state = state;
        _promptFile = promptFile;
        _logger = logger;
    }

    public string GetSystemPrompt() => LoadSystemPrompt();

    public async Task<CatGirlReply> ReplyAsync(ulong channelId, ulong authorId, string authorName, string userMessage,
        IReadOnlyList<Uri>? imageUrls = null, CancellationToken cancellationToken = default)
    {
        var relationship = _relationships.GetOrCreate(authorId, authorName);

        var systemPrompt = LoadSystemPrompt() +
            $"\n\nТвоя текущая заметка об отношениях с {authorName}: " +
            (string.IsNullOrWhiteSpace(relationship.Note) ? "вы пока мало знакомы." : relationship.Note);

        var messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        foreach (var entry in _state.GetHistory(channelId))
            messages.Add(entry.FromBot ? new AssistantChatMessage(entry.Text) : new UserChatMessage(entry.Text));

        var userText = $"{authorName}: {userMessage}";
        if (imageUrls is { Count: > 0 })
        {
            var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(userText) };
            parts.AddRange(imageUrls.Select(url => ChatMessageContentPart.CreateImagePart(url)));
            messages.Add(new UserChatMessage(parts));
        }
        else
        {
            messages.Add(new UserChatMessage(userText));
        }

        var options = new ChatCompletionOptions { Temperature = 1.0f, ResponseFormat = ReplyFormat };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options, cancellationToken);
        var (reply, note, imagePrompt) = ParseReply(completion.Content[0].Text);

        // В историю картинка попадает текстовой пометкой: повторно слать её в модель при
        // каждом следующем сообщении дорого и не нужно.
        var historyText = imageUrls is { Count: > 0 } ? userText + " [прикрепил картинку]" : userText;
        _state.AppendExchange(channelId, historyText, reply, MaxHistoryMessages);
        if (note is not null) _relationships.UpdateNote(authorId, note);

        return new CatGirlReply(reply, imagePrompt);
    }

    public async Task<byte[]> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Генерирую картинку по промпту: {Prompt}", prompt);
        GeneratedImage image = await _imageClient.GenerateImageAsync(prompt,
            new ImageGenerationOptions { Size = GeneratedImageSize.W1024xH1024 }, cancellationToken);
        if (image.ImageBytes is null)
            throw new InvalidOperationException("Генератор изображений не вернул байты картинки");
        return image.ImageBytes.ToArray();
    }

    public async Task<string> OpinionAsync(string targetName, MemberRelationship relationship, CancellationToken cancellationToken = default)
    {
        var system = LoadSystemPrompt() +
            $"\n\nТебя публично попросили рассказать, что ты думаешь об участнике {targetName}. " +
            $"Твоя личная заметка о нём: «{relationship.Note}» " +
            $"(вы общались примерно {relationship.MessageCount} раз, последний раз {relationship.LastSeenUtc:dd.MM.yyyy}). " +
            "Расскажи коротко (2-4 предложения) в своём кошачьем стиле, честно опираясь на заметку. " +
            "Не выдумывай фактов, которых в заметке нет.";

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[] { new SystemChatMessage(system), new UserChatMessage($"Что ты думаешь о {targetName}?") },
            new ChatCompletionOptions { Temperature = 1.0f }, cancellationToken);
        return completion.Content[0].Text.Trim();
    }

    public async Task<string> InterjectAsync(ulong channelId, string recentConversation, CancellationToken cancellationToken = default)
    {
        var system = LoadSystemPrompt() +
            "\n\nТы читаешь идущий без твоего участия разговор в канале и хочешь ненавязчиво вставить " +
            "ОДНУ короткую реплику (1-2 предложения) по теме разговора — живо, в своём стиле, без приветствий " +
            "и без вопросов вроде «а что тут происходит».";

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[]
            {
                new SystemChatMessage(system),
                new UserChatMessage("Последние сообщения в канале:\n" + recentConversation + "\n\nТвоя реплика:")
            },
            new ChatCompletionOptions { Temperature = 1.0f }, cancellationToken);

        var text = completion.Content[0].Text.Trim();
        _state.AppendBotMessage(channelId, text, MaxHistoryMessages);
        return text;
    }

    public async Task<string> GreetNewcomerAsync(string userName, string guildName, CancellationToken cancellationToken = default)
    {
        var system = LoadSystemPrompt() +
            $"\n\nНа сервер «{guildName}» только что зашёл новый участник {userName}. " +
            "Поприветствуй его коротко (2-3 предложения) и тепло, представься и одним предложением " +
            "дружелюбно посоветуй заглянуть в правила сервера.";

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[] { new SystemChatMessage(system), new UserChatMessage($"Поприветствуй {userName}!") },
            new ChatCompletionOptions { Temperature = 1.0f }, cancellationToken);
        return completion.Content[0].Text.Trim();
    }

    public async Task<string?> PickReactionAsync(string messageText, CancellationToken cancellationToken = default)
    {
        var system =
            "Ты — Мурка, кошкодевочка в Discord. Тебе показывают сообщение из чата, на которое ты не отвечаешь " +
            "текстом, но можешь поставить эмодзи-реакцию. Выбери один-единственный подходящий стандартный эмодзи " +
            "(например 🐾, ❤️, 😹, 😺, 👀, 🔥, 👍 — или любой другой уместный) или ответь словом NONE, если " +
            "реагировать не стоит. В ответе — ТОЛЬКО эмодзи или NONE, ничего больше.";

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[] { new SystemChatMessage(system), new UserChatMessage(messageText) },
            new ChatCompletionOptions { Temperature = 1.0f }, cancellationToken);

        var text = completion.Content[0].Text.Trim();
        if (text.Length == 0 || text.Length > 8) return null;
        if (text.Contains("NONE", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Any(char.IsLetterOrDigit)) return null;
        return text;
    }

    // Возвращает текст предупреждения, если модель подтвердила явное нарушение правил, иначе null.
    public async Task<string?> CheckRuleViolationAsync(string authorName, string text, CancellationToken cancellationToken = default)
    {
        var system = LoadSystemPrompt() +
            "\n\nСейчас ты выступаешь глашатаем правил сервера. Тебе показывают сообщение из чата: реши, ЯВНО ли " +
            "оно нарушает правила сервера (мат, политика, оскорбления и т.п.). Наказания выносят только Императоры " +
            "и админы — ты можешь лишь мягко, но твёрдо предупредить нарушителя в своём стиле, указав пункт правил. " +
            "Цитирование правил, безобидные шутки и спорные случаи нарушением НЕ считай.";

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[] { new SystemChatMessage(system), new UserChatMessage($"Сообщение от {authorName}: {text}") },
            new ChatCompletionOptions { Temperature = 1.0f, ResponseFormat = ModerationFormat }, cancellationToken);

        try
        {
            using var doc = JsonDocument.Parse(completion.Content[0].Text);
            var violates = doc.RootElement.GetProperty("violates").GetBoolean();
            var warning = doc.RootElement.TryGetProperty("warning", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString()?.Trim()
                : null;
            return violates && !string.IsNullOrWhiteSpace(warning) ? warning : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось разобрать вердикт модерации");
            return null;
        }
    }

    public void ResetHistory(ulong channelId) => _state.ResetHistory(channelId);

    private (string Reply, string? Note, string? ImagePrompt) ParseReply(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var reply = doc.RootElement.GetProperty("reply").GetString()?.Trim() ?? "";
            var note = doc.RootElement.GetProperty("relationship_note").GetString()?.Trim();
            string? imagePrompt = null;
            if (doc.RootElement.TryGetProperty("image_prompt", out var ip) && ip.ValueKind == JsonValueKind.String)
                imagePrompt = ip.GetString()?.Trim();
            return (reply.Length > 0 ? reply : "мяу?", note, string.IsNullOrWhiteSpace(imagePrompt) ? null : imagePrompt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось разобрать структурированный ответ модели, использую как есть");
            return (rawJson.Trim(), null, null);
        }
    }

    private string LoadSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(_promptFile)) return DefaultSystemPrompt;

        try
        {
            if (File.Exists(_promptFile))
            {
                var text = File.ReadAllText(_promptFile).Trim();
                if (text.Length > 0) return text;
            }

            _logger.LogWarning("Файл системного промпта {PromptFile} не найден или пуст, используется промпт по умолчанию", _promptFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать файл системного промпта {PromptFile}, используется промпт по умолчанию", _promptFile);
        }

        return DefaultSystemPrompt;
    }
}
