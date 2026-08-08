FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# ffmpeg/ffprobe are pinned static builds, not distro packages: the GIF preview pipeline decodes
# animated WebP, which needs ffmpeg >= 9.0 — no 6.x/7.x/8.x release has the decoder, so no distro
# repo qualifies (noble ships 6.1, resolute 8.0; verified against real library files 2026-08-08).
# yt-dlp and every other media path use these same binaries via PATH.
COPY --from=mwader/static-ffmpeg:9.0 /ffmpeg /ffprobe /usr/local/bin/
RUN apt-get update && apt-get install -y --no-install-recommends python3 curl \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Yap.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Keep a copy of default config for first-run
COPY --from=publish /app/publish/appsettings.json ./appsettings.default.json
# Copy and set up entrypoint
COPY docker-entrypoint.sh .
RUN chmod +x docker-entrypoint.sh
ENTRYPOINT ["./docker-entrypoint.sh"]
