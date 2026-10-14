# rot watcher — fly.io worker image. Stage 1 builds the Discord exporter for Linux; stage 2 runs the
# 60s capture -> commit -> push loop 24/7 (immune to laptop sleep). No inbound traffic; pure worker.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY tools/dce ./tools/dce
RUN dotnet publish tools/dce/DiscordChatExporter.Cli -c Release -r linux-x64 --self-contained \
      -p:CSharpier_Bypass=true -p:PublishTrimmed=false -o /out

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      git ca-certificates libicu72 libssl3 tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out /app/dce
COPY fly/entrypoint.sh /app/entrypoint.sh
COPY fly/askpass.sh /app/askpass.sh
RUN chmod +x /app/entrypoint.sh /app/askpass.sh /app/dce/DiscordChatExporter.Cli
ENV TZ=America/New_York DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ENTRYPOINT ["/app/entrypoint.sh"]
