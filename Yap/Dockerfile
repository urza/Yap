FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# ffmpeg/ffprobe are pinned static builds, not distro packages: the GIF preview pipeline decodes
# animated WebP, which needs ffmpeg >= 9.0 — no 6.x/7.x/8.x release has the decoder, so no distro
# repo qualifies (noble ships 6.1, resolute 8.0; verified against real library files 2026-08-08).
# yt-dlp and every other media path use these same binaries via PATH.
COPY --from=mwader/static-ffmpeg:9.0 /ffmpeg /ffprobe /usr/local/bin/
# yt-dlp_linux (PyInstaller build), NOT the plain zipimport binary: only the standalone
# build bundles curl_cffi, which the TikTok extractor needs to impersonate a browser.
# Without it TikTok answers server IPs with a challenge page and extraction fails
# (verified on live 2026-09-02). The standalone build also removes the python3 dependency.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp \
    && chmod a+rx /usr/local/bin/yt-dlp \
    && rm -rf /var/lib/apt/lists/*
# Deno is yt-dlp's JavaScript runtime for YouTube. Recent yt-dlp releases have the YouTube extractor
# solve the player challenge in JS, which needs a runtime on PATH; without one the metadata probe
# exits 1 with "No supported JavaScript runtime could be found" and every YouTube link silently
# gets no embed (seen on live 2026-09-06). Deno is the only runtime yt-dlp enables by default.
# Pinned bare binary from the official image, same pattern as ffmpeg above.
COPY --from=denoland/deno:bin-2.9.6 /deno /usr/local/bin/deno
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
