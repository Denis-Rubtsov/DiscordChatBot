using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

class DiscordBotService
{
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly CatGirlAiService _ai;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly ConcurrentDictionary<ulong, byte> _activeChannels = new();

    public DiscordBotService(DiscordSocketClient client, CatGirlAiService ai, ILogger<DiscordBotService> logger)
    {
        _client = client;
        _ai = ai;
        _logger = logger;

        _client.Log += OnLog;
        _client.Ready += OnReady;
        _client.MessageReceived += OnMessageReceived;
    }

    public async Task StartAsync(string token)
    {
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    private Task OnReady()
    {
        _logger.LogInformation("Мурка на связи как {Username}", _client.CurrentUser);
        return Task.CompletedTask;
    }

    private Task OnLog(LogMessage log)
    {
        var level = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace
        };
        _logger.Log(level, log.Exception, "{Source}: {Message}", log.Source, log.Message);
        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message) return;
        if (message.Author.Id == _client.CurrentUser.Id || message.Author.IsBot) return;

        var isDirectMessage = message.Channel is IDMChannel;
        var isMentioned = message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
        var isActiveChannel = _activeChannels.ContainsKey(message.Channel.Id);
        if (!isDirectMessage && !isMentioned && !isActiveChannel) return;

        if (isMentioned) _activeChannels.TryAdd(message.Channel.Id, 0);

        var content = message.Content;
        foreach (var mentionedUser in message.MentionedUsers.Where(u => u.Id == _client.CurrentUser.Id))
            content = content.Replace(mentionedUser.Mention, "", StringComparison.OrdinalIgnoreCase);
        content = content.Trim();

        if (content.Length == 0) content = "мяу";

        try
        {
            var reply = await _ai.ReplyAsync(message.Channel.Id, message.Author.GlobalName ?? message.Author.Username, content);
            await SendInChunksAsync(message.Channel, reply);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сгенерировать ответ в канале {ChannelId}", message.Channel.Id);
            await message.Channel.SendMessageAsync("мяу... что-то пошло не так, попробуй написать ещё раз 🐾");
        }
    }

    private static async Task SendInChunksAsync(IMessageChannel channel, string text)
    {
        if (text.Length <= DiscordMessageLimit)
        {
            await channel.SendMessageAsync(text);
            return;
        }

        for (var i = 0; i < text.Length; i += DiscordMessageLimit)
            await channel.SendMessageAsync(text.Substring(i, Math.Min(DiscordMessageLimit, text.Length - i)));
    }
}
