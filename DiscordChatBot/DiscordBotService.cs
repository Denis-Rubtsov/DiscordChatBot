using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

class DiscordBotService
{
    private const int DiscordMessageLimit = 2000;

    private readonly DiscordSocketClient _client;
    private readonly CatGirlAiService _ai;
    private readonly VoiceChatService _voice;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly ConcurrentDictionary<ulong, byte> _activeChannels = new();

    public DiscordBotService(DiscordSocketClient client, CatGirlAiService ai, VoiceChatService voice, ILogger<DiscordBotService> logger)
    {
        _client = client;
        _ai = ai;
        _voice = voice;
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

    // Discord.Net awaits this handler's Task directly on the gateway's message-processing
    // loop; anything slower than a couple of seconds here delays heartbeats and can get the
    // connection dropped ("A MessageReceived handler is blocking the gateway task"). The AI
    // call routinely takes longer than that, so it must run on a detached task, never awaited
    // by this handler.
    private Task OnMessageReceived(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message) return Task.CompletedTask;
        if (message.Author.Id == _client.CurrentUser.Id || message.Author.IsBot) return Task.CompletedTask;

        if (message.Author is SocketGuildUser guildUser)
        {
            var command = message.Content.Trim();
            if (command.Equals("!войс", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => HandleVoiceJoinAsync(message, guildUser));
                return Task.CompletedTask;
            }
            if (command.Equals("!развойс", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => HandleVoiceLeaveAsync(message, guildUser));
                return Task.CompletedTask;
            }
        }

        var isDirectMessage = message.Channel is IDMChannel;
        var isMentioned = message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
        var isActiveChannel = _activeChannels.ContainsKey(message.Channel.Id);
        if (!isDirectMessage && !isMentioned && !isActiveChannel) return Task.CompletedTask;

        if (isMentioned) _activeChannels.TryAdd(message.Channel.Id, 0);

        _ = Task.Run(() => HandleMessageAsync(message));
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(SocketUserMessage message)
    {
        var content = message.Content;
        foreach (var mentionedUser in message.MentionedUsers.Where(u => u.Id == _client.CurrentUser.Id))
            content = content.Replace(mentionedUser.Mention, "", StringComparison.OrdinalIgnoreCase);
        content = content.Trim();

        if (content.Length == 0) content = "мяу";

        _logger.LogInformation("Обрабатываю сообщение от {Author} в канале {ChannelId}", message.Author.Username, message.Channel.Id);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reply = await _ai.ReplyAsync(message.Channel.Id, message.Author.Id, message.Author.GlobalName ?? message.Author.Username, content, cts.Token);
            await SendInChunksAsync(message.Channel, reply);
            _logger.LogInformation("Ответил в канале {ChannelId}", message.Channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сгенерировать ответ в канале {ChannelId}", message.Channel.Id);
            await message.Channel.SendMessageAsync("мяу... что-то пошло не так, попробуй написать ещё раз 🐾");
        }
    }

    private async Task HandleVoiceJoinAsync(SocketUserMessage message, SocketGuildUser guildUser)
    {
        var voiceChannel = guildUser.VoiceChannel;
        if (voiceChannel is null)
        {
            await message.Channel.SendMessageAsync("мяу, ты сначала сам зайди в голосовой канал 🐾");
            return;
        }

        if (_voice.IsActive(voiceChannel.Guild.Id))
        {
            await message.Channel.SendMessageAsync("я уже в голосовом мяу");
            return;
        }

        try
        {
            await _voice.JoinAsync(voiceChannel);
            await message.Channel.SendMessageAsync($"мур, захожу в «{voiceChannel.Name}» 🎙️");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось подключиться к голосовому каналу {Channel}", voiceChannel.Name);
            await message.Channel.SendMessageAsync("мяу... не получилось зайти в голосовой, попробуй ещё раз 🐾");
        }
    }

    private async Task HandleVoiceLeaveAsync(SocketUserMessage message, SocketGuildUser guildUser)
    {
        if (!_voice.IsActive(guildUser.Guild.Id))
        {
            await message.Channel.SendMessageAsync("я и не в голосовом мяу");
            return;
        }

        await _voice.LeaveAsync(guildUser.Guild.Id);
        await message.Channel.SendMessageAsync("окей, ухожу из голосового мяу 👋");
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
