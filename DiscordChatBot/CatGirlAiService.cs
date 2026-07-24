using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

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
                }
            },
            "required": ["reply", "relationship_note"],
            "additionalProperties": false
        }
        """),
        jsonSchemaFormatDescription: "Ответ кошкодевочки вместе с обновлённой заметкой об отношениях с собеседником",
        jsonSchemaIsStrict: true);

    private readonly ChatClient _client;
    private readonly RelationshipStore _relationships;
    private readonly string? _promptFile;
    private readonly ILogger<CatGirlAiService> _logger;
    private readonly Dictionary<ulong, List<ChatMessage>> _history = new();
    private readonly object _historyLock = new();

    public CatGirlAiService(string apiKey, string model, string? promptFile, RelationshipStore relationships, ILogger<CatGirlAiService> logger)
    {
        _client = new ChatClient(model, apiKey);
        _relationships = relationships;
        _promptFile = promptFile;
        _logger = logger;
    }

    public async Task<string> ReplyAsync(ulong channelId, ulong authorId, string authorName, string userMessage)
    {
        var relationship = _relationships.GetOrCreate(authorId, authorName);

        var systemPrompt = LoadSystemPrompt() +
            $"\n\nТвоя текущая заметка об отношениях с {authorName}: " +
            (string.IsNullOrWhiteSpace(relationship.Note) ? "вы пока мало знакомы." : relationship.Note);

        var messages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };

        lock (_historyLock)
        {
            if (_history.TryGetValue(channelId, out var existing))
                messages.AddRange(existing);
        }

        messages.Add(new UserChatMessage($"{authorName}: {userMessage}"));

        var options = new ChatCompletionOptions { Temperature = 1.0f, ResponseFormat = ReplyFormat };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);
        var (reply, note) = ParseReply(completion.Content[0].Text);

        RememberExchange(channelId, authorName, userMessage, reply);
        if (note is not null) _relationships.UpdateNote(authorId, note);

        return reply;
    }

    public void ResetHistory(ulong channelId)
    {
        lock (_historyLock)
        {
            _history.Remove(channelId);
        }
    }

    private (string Reply, string? Note) ParseReply(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var reply = doc.RootElement.GetProperty("reply").GetString()?.Trim() ?? "";
            var note = doc.RootElement.GetProperty("relationship_note").GetString()?.Trim();
            return (reply.Length > 0 ? reply : "мяу?", note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось разобрать структурированный ответ модели, использую как есть");
            return (rawJson.Trim(), null);
        }
    }

    private void RememberExchange(ulong channelId, string authorName, string userMessage, string reply)
    {
        lock (_historyLock)
        {
            if (!_history.TryGetValue(channelId, out var list))
            {
                list = new List<ChatMessage>();
                _history[channelId] = list;
            }

            list.Add(new UserChatMessage($"{authorName}: {userMessage}"));
            list.Add(new AssistantChatMessage(reply));

            if (list.Count > MaxHistoryMessages)
                list.RemoveRange(0, list.Count - MaxHistoryMessages);
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
