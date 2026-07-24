using System.Collections.Concurrent;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using OpenAI.Realtime;

// Bridges a Discord voice channel to the OpenAI Realtime API: Discord.Net already hands each
// speaking member's audio as decoded PCM (48kHz stereo) via StreamCreated, so we just
// downsample it to 24kHz mono for the model, and upsample the model's 24kHz mono replies back
// to 48kHz stereo for Discord's output stream. Server-side voice activity detection
// (RealtimeServerVadTurnDetection) drives turn-taking, so there is no manual
// "is someone still talking" logic here.
class VoiceChatService
{
    private const string Instructions =
        "Ты — Мурка, милая игривая кошкодевочка, разговариваешь голосом в Discord-канале. " +
        "Говори по-русски (если с тобой говорят на другом языке — переходи на него), коротко и живо, " +
        "тёплым дружелюбным тоном, изредка мяукай или мурлыкай, но не в каждой реплике. Не растягивай ответы.";

    private readonly RealtimeClient _realtimeClient;
    private readonly string _realtimeModel;
    private readonly string _voice;
    private readonly ILogger<VoiceChatService> _logger;
    private readonly ConcurrentDictionary<ulong, VoiceSession> _sessions = new();

    public VoiceChatService(string apiKey, string realtimeModel, string voice, ILogger<VoiceChatService> logger)
    {
        _realtimeClient = new RealtimeClient(apiKey);
        _realtimeModel = realtimeModel;
        _voice = voice;
        _logger = logger;
    }

    public bool IsActive(ulong guildId) => _sessions.ContainsKey(guildId);

    public async Task JoinAsync(SocketVoiceChannel channel)
    {
        var guildId = channel.Guild.Id;
        if (_sessions.ContainsKey(guildId)) return;

        _logger.LogInformation("Подключаюсь к голосовому каналу {Channel} на сервере {Guild}", channel.Name, channel.Guild.Name);

        var audioClient = await channel.ConnectAsync(selfDeaf: false);
        var cts = new CancellationTokenSource();
        var session = new VoiceSession(channel, audioClient, cts);
        _sessions[guildId] = session;

        // Регистрируем обработчики СРАЗУ после подключения, а не после настройки Realtime-сессии:
        // пока идут сетевые вызовы к OpenAI (пара секунд), уже говорящие участники успевают
        // получить свой StreamCreated от Discord.Net, и если подписаться позже — этот вызов для
        // них больше не повторится за всю голосовую сессию, и их аудио никогда не долетит до модели.
        audioClient.StreamCreated += (userId, inStream) => OnUserStreamCreated(session, userId, inStream);

        var realtimeSession = await _realtimeClient.StartConversationSessionAsync(_realtimeModel, options: null, CancellationToken.None);

        await realtimeSession.ConfigureConversationSessionAsync(new RealtimeConversationSessionOptions
        {
            Instructions = Instructions,
            OutputModalities = { RealtimeOutputModality.Audio },
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = new RealtimeConversationSessionInputAudioOptions
                {
                    // PCM для Realtime API всегда 24kHz/16-bit/mono, Rate у формата read-only.
                    AudioFormat = new RealtimePcmAudioFormat(),
                    TurnDetection = new RealtimeServerVadTurnDetection
                    {
                        CreateResponseEnabled = true,
                        InterruptResponseEnabled = true
                    }
                },
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    Voice = new RealtimeVoice(_voice)
                }
            }
        }, CancellationToken.None);

        session.RealtimeSession = realtimeSession;
        session.DiscordOut = audioClient.CreatePCMStream(AudioApplication.Voice, bitrate: null, bufferMillis: 1000, packetLoss: 5);

        session.PumpTask = Task.Run(() => PumpModelAudioToDiscordAsync(session));
        _logger.LogInformation("Голосовая сессия полностью готова в {Channel}", channel.Name);
    }

    public async Task LeaveAsync(ulong guildId)
    {
        if (!_sessions.TryRemove(guildId, out var session)) return;

        _logger.LogInformation("Выхожу из голосового канала {Channel}", session.Channel.Name);

        session.Cts.Cancel();
        try { await session.PumpTask; } catch { /* задача просто прервана отменой */ }

        session.RealtimeSession?.Dispose();
        await session.AudioClient.StopAsync();
        await session.Channel.DisconnectAsync();
        session.Cts.Dispose();
    }

    private Task OnUserStreamCreated(VoiceSession session, ulong userId, AudioInStream inStream)
    {
        if (userId == session.Channel.Guild.CurrentUser.Id) return Task.CompletedTask;

        _logger.LogInformation("Начал слушать участника {UserId}", userId);
        _ = Task.Run(() => PumpUserAudioAsync(session, userId, inStream));
        return Task.CompletedTask;
    }

    // AudioInStream из StreamCreated отдаёт уже декодированный Discord.Net'ом PCM (48kHz stereo
    // 16-bit) — RTPFrame.Payload здесь не сырой Opus. Оборачивать его в свой OpusDecodeStream не
    // нужно и неверно: тот класс не принимает заголовки от внешнего кода (WriteHeader на нём
    // бросает "This stream does not accept headers" — это внутренний строительный блок, которым
    // Discord.Net уже пользуется сам при создании этого самого стрима).
    private async Task PumpUserAudioAsync(VoiceSession session, ulong userId, AudioInStream inStream)
    {
        var sentBytes = 0L;
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                var frame = await inStream.ReadFrameAsync(session.Cts.Token);
                if (frame.Missed || frame.Payload.Length == 0) continue;

                var mono24k = DownsampleStereo48kToMono24k(frame.Payload, 0, frame.Payload.Length);
                if (mono24k.Length > 0 && session.RealtimeSession is { } realtimeSession)
                {
                    try
                    {
                        await realtimeSession.SendInputAudioAsync(BinaryData.FromBytes(mono24k), session.Cts.Token);
                        sentBytes += mono24k.Length;
                        if (sentBytes % 200_000 < mono24k.Length)
                            _logger.LogInformation("Отправлено в Realtime API уже {Bytes} байт от {UserId}", sentBytes, userId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось отправить аудио в Realtime API");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* ожидаемо при выходе из канала */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Обрыв при чтении голоса участника {UserId}", userId);
        }
    }

    private async Task PumpModelAudioToDiscordAsync(VoiceSession session)
    {
        // На момент вызова оба поля уже установлены в JoinAsync — метод запускается только после этого.
        var realtimeSession = session.RealtimeSession!;
        var discordOut = session.DiscordOut!;

        var receivedAudioBytes = 0L;
        var writtenBytes = 0L;
        try
        {
            await foreach (var update in realtimeSession.ReceiveUpdatesAsync(session.Cts.Token))
            {
                switch (update)
                {
                    case RealtimeServerUpdateResponseOutputAudioDelta delta:
                        receivedAudioBytes += delta.Delta.ToArray().Length;
                        var stereo48k = UpsampleMono24kToStereo48k(delta.Delta.ToArray());
                        await discordOut.WriteAsync(stereo48k, 0, stereo48k.Length, session.Cts.Token);
                        writtenBytes += stereo48k.Length;
                        break;
                    case RealtimeServerUpdateError error:
                        _logger.LogWarning("Realtime API вернул ошибку: {Message}", error.Error.Message);
                        break;
                    case RealtimeServerUpdateInputAudioBufferSpeechStarted:
                        _logger.LogInformation("Realtime: услышала начало речи");
                        break;
                    case RealtimeServerUpdateInputAudioBufferSpeechStopped:
                        _logger.LogInformation("Realtime: услышала конец речи");
                        break;
                    case RealtimeServerUpdateResponseCreated:
                        _logger.LogInformation("Realtime: начинает генерировать ответ");
                        break;
                    case RealtimeServerUpdateResponseDone:
                        _logger.LogInformation("Realtime: ответ завершён, получено {Received} байт аудио, записано в Discord {Written} байт", receivedAudioBytes, writtenBytes);
                        receivedAudioBytes = 0;
                        writtenBytes = 0;
                        break;
                    case RealtimeServerUpdateSessionCreated:
                        _logger.LogInformation("Realtime: сессия создана и сконфигурирована");
                        break;
                    default:
                        _logger.LogInformation("Realtime: событие {Type}", update.GetType().Name);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* ожидаемо при выходе из канала */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Голосовая сессия упала на сервере {Guild}", session.Channel.Guild.Name);
        }
    }

    // 48kHz stereo 16-bit PCM -> 24kHz mono 16-bit PCM: усредняем L/R, берём каждый второй семпл.
    private static byte[] DownsampleStereo48kToMono24k(byte[] buffer, int offset, int count)
    {
        var stereoSampleCount = count / 4; // 4 bytes per stereo frame (2ch * 16bit)
        var outSamples = stereoSampleCount / 2; // downsample by 2x
        var result = new byte[outSamples * 2];

        for (int i = 0, outIdx = 0; i < outSamples; i++)
        {
            var frameOffset = offset + i * 2 * 4; // берём каждый второй стерео-фрейм
            short left = (short)(buffer[frameOffset] | (buffer[frameOffset + 1] << 8));
            short right = (short)(buffer[frameOffset + 2] | (buffer[frameOffset + 3] << 8));
            short mono = (short)((left + right) / 2);

            result[outIdx++] = (byte)(mono & 0xFF);
            result[outIdx++] = (byte)((mono >> 8) & 0xFF);
        }

        return result;
    }

    // 24kHz mono 16-bit PCM -> 48kHz stereo 16-bit PCM: линейная интерполяция между семплами + дублирование в оба канала.
    private static byte[] UpsampleMono24kToStereo48k(byte[] mono24k)
    {
        var inSamples = mono24k.Length / 2;
        if (inSamples == 0) return Array.Empty<byte>();

        var result = new byte[inSamples * 2 * 4]; // 2x семплов, 2 канала, 2 байта
        var outIdx = 0;

        for (var i = 0; i < inSamples; i++)
        {
            short current = (short)(mono24k[i * 2] | (mono24k[i * 2 + 1] << 8));
            short next = i + 1 < inSamples
                ? (short)(mono24k[(i + 1) * 2] | (mono24k[(i + 1) * 2 + 1] << 8))
                : current;
            short interpolated = (short)((current + next) / 2);

            WriteStereoSample(result, ref outIdx, current);
            WriteStereoSample(result, ref outIdx, interpolated);
        }

        return result;
    }

    private static void WriteStereoSample(byte[] buffer, ref int idx, short sample)
    {
        var lo = (byte)(sample & 0xFF);
        var hi = (byte)((sample >> 8) & 0xFF);
        buffer[idx++] = lo;
        buffer[idx++] = hi;
        buffer[idx++] = lo;
        buffer[idx++] = hi;
    }

    private class VoiceSession
    {
        public SocketVoiceChannel Channel { get; }
        public IAudioClient AudioClient { get; }
        // Заполняются чуть позже конструктора (после настройки Realtime-сессии) — StreamCreated
        // может успеть сработать раньше, поэтому оба поля nullable, а не readonly-параметры ctor.
        public RealtimeSessionClient? RealtimeSession { get; set; }
        public AudioOutStream? DiscordOut { get; set; }
        public CancellationTokenSource Cts { get; }
        public Task PumpTask { get; set; } = Task.CompletedTask;

        public VoiceSession(SocketVoiceChannel channel, IAudioClient audioClient, CancellationTokenSource cts)
        {
            Channel = channel;
            AudioClient = audioClient;
            Cts = cts;
        }
    }
}
