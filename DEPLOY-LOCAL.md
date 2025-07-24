# BlazorChat Local Docker Deployment Guide

This guide is for deploying BlazorChat on a single machine using local Docker builds (no container registry needed).

## Prerequisites
- Docker Desktop installed and running
- Git installed
- Nginx Proxy Manager configured

## Step 1: Clone the Repository

On your Docker machine:

```bash
# Clone the repository
git clone https://github.com/YOUR-USERNAME/BlazorChat.git
cd BlazorChat/BlazorChat

# Or copy the project files to your server
```

## Step 2: Build and Run

1. Create the uploads directory:
```bash
mkdir -p uploads
chmod 777 uploads
```

2. Build and start the containers:
```bash
docker-compose up -d --build
```

This command will:
- Build both images locally
- Start the containers
- The `--build` flag ensures images are rebuilt

### Understanding the Uploads Volume

The docker-compose.yml uses a bind mount for uploaded files:
```yaml
volumes:
  - ./uploads:/app/wwwroot/uploads
```

This means:
- **Host directory**: `./uploads` (relative to docker-compose.yml location)
- **Container directory**: `/app/wwwroot/uploads`
- **Files are stored directly on your host** - you can browse, backup, and manage them normally
- **Persists across container restarts** - removing containers won't delete your uploads

You can also use an absolute path if preferred:
```yaml
volumes:
  - /path/to/my/uploads:/app/wwwroot/uploads
  # Windows example:
  - D:/tmp/blazorchat-uploads:/app/wwwroot/uploads
```

## Step 3: Configure Nginx Proxy Manager

Add a new proxy host:

1. **Domain Names**: `chat.yourdomain.com`
2. **Scheme**: `http`
3. **Forward Hostname/IP**: 
   - Use `blazorchat` (if NPM is on same Docker network)
   - Or use your Docker host IP (e.g., `192.168.1.100`)
4. **Forward Port**: `80`
5. **Enable Websockets Support**: ✓ (Required for SignalR!)

### Custom Nginx Configuration:
```nginx
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

# SignalR specific
proxy_set_header Connection $http_connection;
proxy_http_version 1.1;
proxy_cache_bypass $http_upgrade;
```

## Step 4: Enable SSL

1. In SSL tab, request Let's Encrypt certificate
2. Enable "Force SSL"

## Useful Commands

### View logs:
```bash
docker-compose logs -f
```

### Rebuild after code changes:
```bash
docker-compose down
docker-compose up -d --build
```

### Update from Git:
```bash
git pull
docker-compose up -d --build
```

### Stop everything:
```bash
docker-compose down
```

## Ports

- BlazorChat UI: `http://localhost:5221`
- SignalR Server: `http://localhost:5224`

Access through your domain after Nginx setup.

## Troubleshooting

### If containers won't start:
```bash
docker-compose down -v
docker-compose up -d --build
```

### If uploads aren't working:
```bash
docker exec -it blazorchat-server ls -la /app/wwwroot/uploads
chmod -R 777 uploads/
```

### Check container status:
```bash
docker ps
docker-compose ps
```

## Advantages of Local Build

- No need for container registry
- Faster deployment (no image push/pull)
- Everything stays on your machine
- Easier for single-server deployments

## Production Tips

1. Always use `restart: unless-stopped` in docker-compose.yml
2. Set `ASPNETCORE_ENVIRONMENT=Production`
3. Use a volume for uploads to persist data
4. Regular backups of the uploads folder