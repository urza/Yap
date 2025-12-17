# Yap Deployment Guide

## Prerequisites
- Docker installed
- (Optional) Nginx Proxy Manager for reverse proxy

## Quick Start

### Option 1: Docker Run

```bash
# Build the image
docker build -t yap ./Yap

# Run with persistent uploads
docker run -d \
  --name yap \
  -p 5221:5221 \
  -e ASPNETCORE_URLS=http://+:5221 \
  -v ./uploads:/app/wwwroot/uploads \
  --restart unless-stopped \
  yap
```

Access at `http://localhost:5221`

### Option 2: Docker Compose

Create `docker-compose.yml`:

```yaml
services:
  yap:
    build: ./Yap
    ports:
      - "5221:5221"
    volumes:
      - ./uploads:/app/wwwroot/uploads
    environment:
      - ASPNETCORE_URLS=http://+:5221
      - ASPNETCORE_ENVIRONMENT=Production
    restart: unless-stopped
```

Run:
```bash
docker-compose up -d
```

## Reverse Proxy Setup (Nginx Proxy Manager)

1. **Domain Names**: `chat.yourdomain.com`
2. **Scheme**: `http`
3. **Forward Hostname/IP**: `yap` (container name) or host IP
4. **Forward Port**: `5221`
5. **Enable Websockets Support**: Required for Blazor Server

### Custom Nginx Configuration:
```nginx
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

# Blazor Server specific
proxy_set_header Connection $http_connection;
proxy_http_version 1.1;
proxy_cache_bypass $http_upgrade;
```

## SSL Certificate

1. In Nginx Proxy Manager SSL tab, request Let's Encrypt certificate
2. Enable "Force SSL"

## Updating

```bash
# Pull latest code
git pull

# Rebuild and restart
docker-compose down
docker-compose up -d --build
```

## Backup

Important data to backup:
- `./uploads/` directory (uploaded images)

## Troubleshooting

### Container won't start
```bash
docker logs yap
docker-compose logs -f
```

### Permission issues with uploads
```bash
chmod -R 777 uploads/
```

### Blazor connection issues
- Ensure WebSockets are enabled in reverse proxy
- Check that the container is healthy: `docker ps`

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Runtime environment |
| `ASPNETCORE_URLS` | `http://+:5221` | Listen URL |

## Production Tips

1. Always use `restart: unless-stopped`
2. Set `ASPNETCORE_ENVIRONMENT=Production`
3. Use a volume for uploads to persist data
4. Enable HTTPS via reverse proxy
5. Regular backups of the uploads folder
