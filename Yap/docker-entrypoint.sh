#!/bin/sh

# Copy default appsettings.json to Data folder if not exists
if [ ! -f /app/Data/appsettings.json ]; then
    echo "Creating default appsettings.json in Data folder..."
    mkdir -p /app/Data
    cp /app/appsettings.default.json /app/Data/appsettings.json
fi

# Run the app
exec dotnet Yap.dll
