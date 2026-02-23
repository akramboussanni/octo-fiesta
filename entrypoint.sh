#!/bin/sh
set -e

# Auto-update yt-dlp to the latest release at every container start.
# This means you never need to rebuild the Docker image just to get a newer yt-dlp.
# If the update check fails (e.g. no internet at startup), the bundled version is used instead.
echo "[octo-fiesta] Checking for yt-dlp updates..."
if yt-dlp -U; then
    echo "[octo-fiesta] yt-dlp is up to date."
else
    echo "[octo-fiesta] yt-dlp update check failed — using existing version."
fi

# Start the .NET application
exec dotnet octo-fiesta.dll
