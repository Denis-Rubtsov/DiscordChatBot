# Сборка: podman build -t localhost/discordchatbot .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DiscordChatBot/DiscordChatBot.csproj DiscordChatBot/
RUN dotnet restore DiscordChatBot/DiscordChatBot.csproj
COPY DiscordChatBot/ DiscordChatBot/
RUN dotnet publish DiscordChatBot/DiscordChatBot.csproj -c Release -o /out --no-restore

# aspnet, а не runtime: у всех трёх ботов один базовый слой (в нём есть и обычный .NET runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Голосовой чат Discord.Net подгружает нативные libopus и libsodium по имени libopus.so/libsodium.so,
# а эти симлинки (без .0/.23 в конце) есть только в -dev пакетах.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libopus-dev libsodium-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
# Настоящий appsettings.json (с токенами) монтируется поверх /app/appsettings.json, состояние лежит в /data
USER $APP_UID
ENTRYPOINT ["dotnet", "DiscordChatBot.dll"]
