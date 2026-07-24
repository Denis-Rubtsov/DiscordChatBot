using System.Collections.Concurrent;
using Discord;
using Discord.Audio;
using Discord.Audio.Streams;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using OpenAI.Realtime;

// Bridges a Discord voice channel to the OpenAI Realtime API: decodes each speaking
// member's Opus audio to PCM, downsamples 48kHz stereo -> 24kHz mono for the model,
// and upsamples the model's 24kHz mono replies back to 48kHz stereo for Discord.
// Server-side voice activity detection (RealtimeServerVadTurnDetection) drives turn-taking,
// so there is no manual "is someone still talking" logic here.
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
        var realtimeSession = await _realtimeClient.StartConversationSessionAsync(_realtimeModel, options: null, CancellationToken.None);

        await realtimeSession.ConfigureConversationSessionAsync(new RealtimeConversationSessionOptions
        {
            Instructions = Instructions,
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

        var discordOut = audioClient.CreatePCMStream(AudioApplication.Voice, bitrate: null, bufferMillis: 1000, packetLoss: 5);
        var cts = new CancellationTokenSource();

        var session = new VoiceSession(channel, audioClient, realtimeSession, discordOut, cts);
        _sessions[guildId] = session;

        audioClient.StreamCreated += (userId, inStream) => OnUserStreamCreated(session, userId, inStream);
        audioClient.StreamDestroyed += userId =>
        {
            if (session.UserDecoders.TryRemove(userId, out var decoder)) decoder.Dispose();
            return Task.CompletedTask;
        };

        session.PumpTask = Task.Run(() => PumpModelAudioToDiscordAsync(session));
    }

    public async Task LeaveAsync(ulong guildId)
    {
        if (!_sessions.TryRemove(guildId, out var session)) return;

        _logger.LogInformation("Выхожу из голосового канала {Channel}", session.Channel.Name);

        session.Cts.Cancel();
        try { await session.PumpTask; } catch { /* задача просто прервана отменой */ }

        foreach (var decoder in session.UserDecoders.Values) decoder.Dispose();
        session.RealtimeSession.Dispose();
        await session.AudioClient.StopAsync();
        await session.Channel.DisconnectAsync();
        session.Cts.Dispose();
    }

    private Task OnUserStreamCreated(VoiceSession session, ulong userId, AudioInStream inStream)
    {
        if (userId == session.Channel.Guild.CurrentUser.Id) return Task.CompletedTask;

        var sink = new PcmCaptureSink(async (buffer, offset, count, ct) =>
        {
            var mono24k = DownsampleStereo48kToMono24k(buffer, offset, count);
            if (mono24k.Length > 0)
            {
                try
                {
                    await session.RealtimeSession.SendInputAudioAsync(BinaryData.FromBytes(mono24k), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось отправить аудио в Realtime API");
                }
            }
        });
        var decoder = new OpusDecodeStream(sink);
        session.UserDecoders[userId] = decoder;

        _ = Task.Run(() => PumpUserAudioAsync(session, decoder, inStream));
        return Task.CompletedTask;
    }

    private async Task PumpUserAudioAsync(VoiceSession session, OpusDecodeStream decoder, AudioInStream inStream)
    {
        try
        {
            while (!session.Cts.IsCancellationRequested)
            {
                var frame = await inStream.ReadFrameAsync(session.Cts.Token);
                decoder.WriteHeader(frame.Sequence, frame.Timestamp, frame.Missed);
                await decoder.WriteAsync(frame.Payload, 0, frame.Payload.Length, session.Cts.Token);
            }
        }
        catch (OperationCanceledException) { /* ожидаемо при выходе из канала */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Обрыв при чтении голоса участника");
        }
    }

    private async Task PumpModelAudioToDiscordAsync(VoiceSession session)
    {
        try
        {
            await foreach (var update in session.RealtimeSession.ReceiveUpdatesAsync(session.Cts.Token))
            {
                if (update is RealtimeServerUpdateResponseOutputAudioDelta delta)
                {
                    var stereo48k = UpsampleMono24kToStereo48k(delta.Delta.ToArray());
                    await session.DiscordOut.WriteAsync(stereo48k, 0, stereo48k.Length, session.Cts.Token);
                }
                else if (update is RealtimeServerUpdateError error)
                {
                    _logger.LogWarning("Realtime API вернул ошибку: {Message}", error.Error.Message);
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
        public RealtimeSessionClient RealtimeSession { get; }
        public AudioOutStream DiscordOut { get; }
        public CancellationTokenSource Cts { get; }
        public ConcurrentDictionary<ulong, OpusDecodeStream> UserDecoders { get; } = new();
        public Task PumpTask { get; set; } = Task.CompletedTask;

        public VoiceSession(SocketVoiceChannel channel, IAudioClient audioClient, RealtimeSessionClient realtimeSession, AudioOutStream discordOut, CancellationTokenSource cts)
        {
            Channel = channel;
            AudioClient = audioClient;
            RealtimeSession = realtimeSession;
            DiscordOut = discordOut;
            Cts = cts;
        }
    }

    // Терминальный приёмник PCM для OpusDecodeStream: просто перенаправляет декодированные байты дальше.
    private class PcmCaptureSink : AudioOutStream
    {
        private readonly Func<byte[], int, int, CancellationToken, Task> _onWrite;

        public PcmCaptureSink(Func<byte[], int, int, CancellationToken, Task> onWrite)
        {
            _onWrite = onWrite;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _onWrite(buffer, offset, count, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
