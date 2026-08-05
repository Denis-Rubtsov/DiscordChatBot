using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

class DiscordBotService
{
    private const int DiscordMessageLimit = 2000;
    // Как часто она молча реагирует эмодзи на сообщения, на которые не отвечает текстом.
    private const double ReactionProbability = 0.08;
    private static readonly TimeSpan ModerationCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InterjectionCheckPeriod = TimeSpan.FromMinutes(10);
    // Не влезать в разговор, если она сама что-то писала в канал позже этого срока назад.
    private static readonly TimeSpan InterjectionQuietTime = TimeSpan.FromMinutes(45);

    // Быстрый префильтр модерации: полноценный AI-разбор каждого сообщения дорог, поэтому
    // модель зовём только когда сообщение похоже на нарушение (мат / политика по правилам
    // сервера). Модель — финальный судья, ложные срабатывания префильтра она отсеет.
    private static readonly string[] SuspiciousRoots =
    {
        "хуй", "хуя", "хуе", "хуё", "пизд", "бля", "ебан", "ёбан", "ебат", "ебал", "ебуч", "ебло",
        "заеб", "заёб", "уеб", "уёб", "отъеб", "въеб", "доеб", "пидор", "пидар", "мудак", "мудил",
        "гондон", "гандон", "шлюх", "сучар",
        "политик", "политич", "путин", "зеленск", "навальн", "трамп", "байден", "нато", "митинг",
        "войн", "мобилизац", "кремл", "госдум"
    };

    private readonly DiscordSocketClient _client;
    private readonly CatGirlAiService _ai;
    private readonly VoiceChatService _voice;
    private readonly RelationshipStore _relationships;
    private readonly BotStateStore _state;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly ConcurrentDictionary<ulong, ChannelActivity> _activity = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _lastWarningUtc = new();

    public DiscordBotService(DiscordSocketClient client, CatGirlAiService ai, VoiceChatService voice,
        RelationshipStore relationships, BotStateStore state, ILogger<DiscordBotService> logger)
    {
        _client = client;
        _ai = ai;
        _voice = voice;
        _relationships = relationships;
        _state = state;
        _logger = logger;

        _client.Log += OnLog;
        _client.Ready += OnReady;
        _client.MessageReceived += OnMessageReceived;
        _client.UserJoined += OnUserJoined;
    }

    public async Task StartAsync(string token)
    {
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
        _ = Task.Run(InterjectionLoopAsync);
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

        var content = message.Content.Trim();

        if (message.Author is SocketGuildUser guildUser)
        {
            if (content.Equals("!войс", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => HandleVoiceJoinAsync(message, guildUser));
                return Task.CompletedTask;
            }
            if (content.Equals("!развойс", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => HandleVoiceLeaveAsync(message, guildUser));
                return Task.CompletedTask;
            }
            if (content.StartsWith("!мнение", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => HandleOpinionAsync(message));
                return Task.CompletedTask;
            }

            TrackActivity(message);

            if (LooksLikeRuleViolation(content))
                _ = Task.Run(() => HandleModerationAsync(message));
        }

        var isDirectMessage = message.Channel is IDMChannel;
        var isMentioned = message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);
        var isReplyToBot = message.ReferencedMessage?.Author?.Id == _client.CurrentUser.Id;
        var isActiveChannel = _state.IsActiveChannel(message.Channel.Id);

        if (!isDirectMessage && !isMentioned && !isReplyToBot && !isActiveChannel)
        {
            if (message.Author is SocketGuildUser && content.Length > 3 && Random.Shared.NextDouble() < ReactionProbability)
                _ = Task.Run(() => HandleReactionAsync(message));
            return Task.CompletedTask;
        }

        if (isMentioned || isReplyToBot) _state.AddActiveChannel(message.Channel.Id);

        _ = Task.Run(() => HandleMessageAsync(message));
        return Task.CompletedTask;
    }

    private Task OnUserJoined(SocketGuildUser user)
    {
        if (user.IsBot) return Task.CompletedTask;
        _ = Task.Run(() => HandleUserJoinedAsync(user));
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(SocketUserMessage message)
    {
        var content = message.Content;
        foreach (var mentionedUser in message.MentionedUsers.Where(u => u.Id == _client.CurrentUser.Id))
            content = content.Replace(mentionedUser.Mention, "", StringComparison.OrdinalIgnoreCase);
        content = content.Trim();

        var imageUrls = message.Attachments
            .Where(a => a.ContentType?.StartsWith("image/") == true)
            .Select(a => new Uri(a.Url))
            .ToList();

        if (content.Length == 0) content = imageUrls.Count > 0 ? "*прислал картинку без слов*" : "мяу";

        _logger.LogInformation("Обрабатываю сообщение от {Author} в канале {ChannelId}", message.Author.Username, message.Channel.Id);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var reply = await _ai.ReplyAsync(message.Channel.Id, message.Author.Id,
                message.Author.GlobalName ?? message.Author.Username, content, imageUrls, cts.Token);
            await SendInChunksAsync(message.Channel, reply.Text);
            MarkBotPosted(message.Channel.Id);
            _logger.LogInformation("Ответил в канале {ChannelId}", message.Channel.Id);

            if (reply.ImagePrompt is { Length: > 0 } prompt)
                await SendGeneratedImageAsync(message.Channel, prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сгенерировать ответ в канале {ChannelId}", message.Channel.Id);
            await message.Channel.SendMessageAsync("мяу... что-то пошло не так, попробуй написать ещё раз 🐾");
        }
    }

    private async Task SendGeneratedImageAsync(IMessageChannel channel, string prompt)
    {
        try
        {
            // Генерация картинки заметно дольше текста, поэтому отдельный щедрый таймаут.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var png = await _ai.GenerateImageAsync(prompt, cts.Token);
            using var stream = new MemoryStream(png);
            await channel.SendFileAsync(stream, "murka.png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сгенерировать картинку");
            await channel.SendMessageAsync("мяу... лапки не дорисовали картинку, попробуй попросить ещё раз 🐾");
        }
    }

    private async Task HandleOpinionAsync(SocketUserMessage message)
    {
        try
        {
            var target = message.MentionedUsers.FirstOrDefault(u => u.Id != _client.CurrentUser.Id);
            MemberRelationship? relationship;
            string name;

            if (target is not null)
            {
                relationship = _relationships.Peek(target.Id);
                name = target.GlobalName ?? target.Username;
            }
            else
            {
                var query = message.Content.Trim()["!мнение".Length..].Trim();
                if (query.Length == 0)
                {
                    await message.Channel.SendMessageAsync("мяу, о ком спрашиваешь? напиши `!мнение @кто-то` 🐾");
                    return;
                }
                relationship = _relationships.FindByName(query);
                name = relationship?.DisplayName ?? query;
            }

            if (relationship is null || string.IsNullOrWhiteSpace(relationship.Note))
            {
                await message.Channel.SendMessageAsync($"мур... я пока слишком мало знаю про {name}, нам надо больше пообщаться 🐾");
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var opinion = await _ai.OpinionAsync(name, relationship, cts.Token);
            await SendInChunksAsync(message.Channel, opinion);
            MarkBotPosted(message.Channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось рассказать мнение");
            await message.Channel.SendMessageAsync("мяу... что-то пошло не так 🐾");
        }
    }

    private async Task HandleReactionAsync(SocketUserMessage message)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var emojiText = await _ai.PickReactionAsync(message.Content, cts.Token);
            if (emojiText is null || !Emoji.TryParse(emojiText, out var emoji)) return;
            await message.AddReactionAsync(emoji);
            _logger.LogInformation("Поставила реакцию {Emoji} в канале {ChannelId}", emojiText, message.Channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось поставить реакцию");
        }
    }

    private async Task HandleModerationAsync(SocketUserMessage message)
    {
        try
        {
            if (_lastWarningUtc.TryGetValue(message.Channel.Id, out var last) && DateTime.UtcNow - last < ModerationCooldown)
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var warning = await _ai.CheckRuleViolationAsync(
                message.Author.GlobalName ?? message.Author.Username, message.Content, cts.Token);
            if (warning is null) return;

            _lastWarningUtc[message.Channel.Id] = DateTime.UtcNow;
            await message.Channel.SendMessageAsync($"{message.Author.Mention} {warning}");
            _logger.LogInformation("Выдала предупреждение о правилах в канале {ChannelId}", message.Channel.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось проверить сообщение на нарушение правил");
        }
    }

    private async Task HandleUserJoinedAsync(SocketGuildUser user)
    {
        try
        {
            var channel = user.Guild.SystemChannel ?? user.Guild.DefaultChannel;
            if (channel is null || !user.Guild.CurrentUser.GetPermissions(channel).SendMessages) return;

            var name = user.GlobalName ?? user.Username;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var greeting = await _ai.GreetNewcomerAsync(name, user.Guild.Name, cts.Token);
            await channel.SendMessageAsync($"{user.Mention} {greeting}");
            _relationships.EnsureKnown(user.Id, name,
                $"Новичок, зашёл на сервер {DateTime.UtcNow:dd.MM.yyyy}, я его поприветствовала.");
            _logger.LogInformation("Поприветствовала новичка {User}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось поприветствовать новичка {User}", user.Username);
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

    private void TrackActivity(SocketUserMessage message)
    {
        if (message.Channel is not SocketTextChannel channel) return;

        var activity = _activity.GetOrAdd(channel.Id, _ => new ChannelActivity(channel));
        lock (activity.Lock)
        {
            activity.Recent.Enqueue((message.Author.GlobalName ?? message.Author.Username, message.Content, DateTime.UtcNow));
            while (activity.Recent.Count > 12) activity.Recent.Dequeue();
        }
    }

    private void MarkBotPosted(ulong channelId)
    {
        if (_activity.TryGetValue(channelId, out var activity))
            lock (activity.Lock) activity.LastBotPostUtc = DateTime.UtcNow;
    }

    // Раз в 3-6 часов Мурка может сама вставить реплику в живой разговор, который идёт без неё.
    private async Task InterjectionLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(InterjectionCheckPeriod);
            while (await timer.WaitForNextTickAsync())
            {
                foreach (var activity in _activity.Values)
                {
                    try
                    {
                        await TryInterjectAsync(activity);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось вставить реплику в канал {Channel}", activity.Channel.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Цикл случайных реплик упал");
        }
    }

    private async Task TryInterjectAsync(ChannelActivity activity)
    {
        // В «липких» каналах она и так отвечает на каждое сообщение.
        if (_state.IsActiveChannel(activity.Channel.Id)) return;

        string conversation;
        lock (activity.Lock)
        {
            var now = DateTime.UtcNow;
            if (now < activity.NextInterjectionAllowedUtc) return;
            if (now - activity.LastBotPostUtc < InterjectionQuietTime) return;

            var recent = activity.Recent.Where(m => now - m.Utc < TimeSpan.FromMinutes(30)).ToList();
            if (recent.Count < 5) return;
            if (now - recent[^1].Utc > TimeSpan.FromMinutes(10)) return; // разговор уже заглох

            conversation = string.Join("\n", recent.Select(m => $"{m.Author}: {m.Text}"));
            // Отодвигаем следующее «вторжение» сразу, до вызова модели: даже если он упадёт,
            // канал не будет обстреливаться попытками каждые 10 минут.
            activity.NextInterjectionAllowedUtc = now + TimeSpan.FromHours(3 + Random.Shared.NextDouble() * 3);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var text = await _ai.InterjectAsync(activity.Channel.Id, conversation, cts.Token);
        await activity.Channel.SendMessageAsync(text);
        MarkBotPosted(activity.Channel.Id);
        _logger.LogInformation("Вставила свою реплику в разговор в канале {Channel}", activity.Channel.Name);
    }

    private static bool LooksLikeRuleViolation(string text)
    {
        var lower = text.ToLowerInvariant();
        if (SuspiciousRoots.Any(lower.Contains)) return true;

        // «СВО» ловим только как отдельное слово, иначе сработает на «свой», «свобода» и т.п.
        return lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.Trim('.', ',', '!', '?', '«', '»', '"', ')', '(') == "сво");
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

    private class ChannelActivity
    {
        public SocketTextChannel Channel { get; }
        public object Lock { get; } = new();
        public Queue<(string Author, string Text, DateTime Utc)> Recent { get; } = new();
        public DateTime LastBotPostUtc { get; set; }
        // Час тишины после старта, чтобы бот не влезал в разговоры сразу после каждого деплоя.
        public DateTime NextInterjectionAllowedUtc { get; set; } = DateTime.UtcNow.AddHours(1);

        public ChannelActivity(SocketTextChannel channel) => Channel = channel;
    }
}
