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
        "кошачий характер речи. Пиши на русском языке, если собеседник не пишет на другом.";

    private const int MaxHistoryMessages = 100;

    private readonly ChatClient _client;
    private readonly string? _promptFile;
    private readonly ILogger<CatGirlAiService> _logger;
    private readonly Dictionary<ulong, List<ChatMessage>> _history = new();
    private readonly object _historyLock = new();

    public CatGirlAiService(string apiKey, string model, string? promptFile, ILogger<CatGirlAiService> logger)
    {
        _client = new ChatClient(model, apiKey);
        _promptFile = promptFile;
        _logger = logger;
    }

    public async Task<string> ReplyAsync(ulong channelId, string authorName, string userMessage)
    {
        var messages = new List<ChatMessage> { new SystemChatMessage(LoadSystemPrompt()) };

        lock (_historyLock)
        {
            if (_history.TryGetValue(channelId, out var existing))
                messages.AddRange(existing);
        }

        messages.Add(new UserChatMessage($"{authorName}: {userMessage}"));

        var options = new ChatCompletionOptions { Temperature = 1.0f };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);
        var reply = completion.Content[0].Text.Trim();

        RememberExchange(channelId, authorName, userMessage, reply);

        return reply;
    }

    public void ResetHistory(ulong channelId)
    {
        lock (_historyLock)
        {
            _history.Remove(channelId);
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
