using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", true)
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConfiguration(config.GetSection("Logging"));
    builder.AddConsole();
});

var discordToken = RequireConfig(config, "Discord:Token", "TOKEN_PLACEHOLDER");
var openAiApiKey = RequireConfig(config, "OpenAI:ApiKey", "API_KEY_PLACEHOLDER");
var openAiModel = config["OpenAI:Model"] ?? "gpt-5.4";
var realtimeModel = config["OpenAI:RealtimeModel"] ?? "gpt-realtime-2.1";
var realtimeVoice = config["OpenAI:RealtimeVoice"] ?? "shimmer";
var promptFile = config["SystemPromptFile"];
var relationshipsFile = config["RelationshipsFile"] ?? "relationships.json";

var discordClient = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMessages
        | GatewayIntents.DirectMessages
        | GatewayIntents.MessageContent
        | GatewayIntents.GuildVoiceStates,
    // Discord требует E2EE (DAVE) для голосовых каналов с марта 2026; без этого
    // ConnectAsync к голосовому каналу закрывается с ошибкой 4017. Требует
    // native libdave.so рядом с бинарником (см. README/deploy).
    EnableVoiceDaveEncryption = true
});

var relationships = new RelationshipStore(relationshipsFile);
var ai = new CatGirlAiService(openAiApiKey, openAiModel, promptFile, relationships, loggerFactory.CreateLogger<CatGirlAiService>());
var voice = new VoiceChatService(openAiApiKey, realtimeModel, realtimeVoice, loggerFactory.CreateLogger<VoiceChatService>());
var bot = new DiscordBotService(discordClient, ai, voice, loggerFactory.CreateLogger<DiscordBotService>());

await bot.StartAsync(discordToken);

loggerFactory.CreateLogger("Program").LogInformation("Бот запущен");
await Task.Delay(Timeout.Infinite);

static string RequireConfig(IConfiguration config, string key, string placeholder)
{
    var value = config[key];
    if (string.IsNullOrWhiteSpace(value) || value == placeholder)
        throw new InvalidOperationException(
            $"Не задан {key}: замени плейсхолдер в appsettings.json или задай переменную окружения " +
            $"{key.Replace(":", "__")}");
    return value;
}
