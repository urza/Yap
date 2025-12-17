# Yap Local Deployment Guide

This guide is for running Yap locally without Docker.

## Prerequisites

- .NET 10 SDK
- Git (optional)

## Running Locally

### Development Mode

```bash
cd Yap
dotnet run
```

The app will start and display the URL (typically `https://localhost:5001` or `http://localhost:5000`).

### Watch Mode (Hot Reload)

```bash
cd Yap
dotnet watch
```

Changes to `.razor`, `.cs`, and `.css` files will automatically reload.

### Production Mode

```bash
cd Yap
dotnet run --configuration Release
```

## Building for Deployment

### Self-Contained Deployment

```bash
cd Yap

# Windows
dotnet publish -c Release -r win-x64 --self-contained

# Linux
dotnet publish -c Release -r linux-x64 --self-contained

# macOS
dotnet publish -c Release -r osx-x64 --self-contained
```

Output will be in `bin/Release/net10.0/{runtime}/publish/`

### Framework-Dependent Deployment

```bash
dotnet publish -c Release
```

Requires .NET 10 runtime on the target machine.

## File Storage

Uploaded images are stored in `wwwroot/uploads/`. Ensure this directory:
- Exists before running
- Has write permissions
- Is backed up regularly

## Configuration

Edit `appsettings.json` for customization:

```json
{
  "ChatSettings": {
    "ProjectName": "Yap",
    "RoomName": "lobby"
  }
}
```

For production, use `appsettings.Production.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  }
}
```

## Running as a Service

### Windows (NSSM)

```bash
nssm install Yap "C:\path\to\Yap.exe"
nssm set Yap AppDirectory "C:\path\to"
nssm start Yap
```

### Linux (systemd)

Create `/etc/systemd/system/yap.service`:

```ini
[Unit]
Description=Yap Chat Application
After=network.target

[Service]
WorkingDirectory=/opt/yap
ExecStart=/opt/yap/Yap
Restart=always
RestartSec=10
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000

[Install]
WantedBy=multi-user.target
```

Enable and start:
```bash
sudo systemctl enable yap
sudo systemctl start yap
```

## Troubleshooting

### Port already in use
```bash
# Find process using port
netstat -ano | findstr :5000  # Windows
lsof -i :5000                  # Linux/macOS

# Use different port
dotnet run --urls "http://localhost:5001"
```

### Upload permissions
```bash
# Linux/macOS
chmod 755 wwwroot/uploads

# Windows - ensure IIS_IUSRS has write access
```

### Logs
Check console output or configure file logging in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```
