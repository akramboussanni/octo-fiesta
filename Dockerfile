# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

ARG VERSION=0.0.0-dev

COPY octo-fiesta.sln .
COPY octo-fiesta/octo-fiesta.csproj octo-fiesta/
COPY octo-fiesta.Tests/octo-fiesta.Tests.csproj octo-fiesta.Tests/

RUN dotnet restore

COPY octo-fiesta/ octo-fiesta/
COPY octo-fiesta.Tests/ octo-fiesta.Tests/

RUN dotnet publish octo-fiesta/octo-fiesta.csproj -c Release -p:Version=$VERSION -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Install ffmpeg and yt-dlp for the YouTube provider
# ffmpeg: audio conversion (required by yt-dlp's --audio-format flag)
# yt-dlp: installed via pip to always get the latest version (apt package lags behind)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ffmpeg \
        python3 \
        python3-pip && \
    pip3 install --no-cache-dir --break-system-packages yt-dlp && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/downloads

COPY --from=build /app/publish .
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# entrypoint.sh self-updates yt-dlp on every start, then launches the app
ENTRYPOINT ["/entrypoint.sh"]
