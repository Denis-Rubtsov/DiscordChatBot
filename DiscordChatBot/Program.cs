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
var openAiModel = config["OpenAI:Model"] ?? "gpt-4o-mini";
var promptFile = config["SystemPromptFile"];

var discordClient = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds
        | GatewayIntents.GuildMessages
        | GatewayIntents.DirectMessages
        | GatewayIntents.MessageContent
});

var ai = new CatGirlAiService(openAiApiKey, openAiModel, promptFile, loggerFactory.CreateLogger<CatGirlAiService>());
var bot = new DiscordBotService(discordClient, ai, loggerFactory.CreateLogger<DiscordBotService>());

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
