# BlazorChat Deployment Guide

## Prerequisites
- Docker Desktop installed and running
- GitHub account with repository access
- Nginx Proxy Manager configured

## Step 1: Push Code and Build Images

1. Commit and push your code to GitHub:
```bash
git add .
git commit -m "Add Docker deployment configuration"
git push origin master
```

2. The GitHub workflow will automatically build and push images to ghcr.io

## Step 2: Configure GitHub Container Registry Access

1. Go to your GitHub profile → Settings → Developer settings → Personal access tokens → Tokens (classic)
2. Create a new token with `read:packages` permission
3. Save the token securely

## Step 3: Deploy on Your Docker Desktop

1. Create a deployment directory on your server:
```bash
mkdir ~/blazorchat-deployment
cd ~/blazorchat-deployment
```

2. Create an `.env` file with your GitHub username:
```bash
echo "GITHUB_USER=your-github-username" > .env
```

3. Login to GitHub Container Registry:
```bash
docker login ghcr.io -u YOUR_GITHUB_USERNAME -p YOUR_GITHUB_TOKEN
```

4. Download the production docker-compose file:
```bash
# Copy the docker-compose.prod.yml from this repo to your deployment directory
```

5. Create uploads directory:
```bash
mkdir -p uploads
chmod 777 uploads
```

6. Start the application:
```bash
docker-compose -f docker-compose.prod.yml up -d
```

## Step 4: Configure Nginx Proxy Manager

Add a new proxy host in Nginx Proxy Manager:

1. **Domain Names**: Your domain (e.g., `chat.yourdomain.com`)
2. **Scheme**: `http`
3. **Forward Hostname/IP**: `blazorchat` (container name)
4. **Forward Port**: `8080`
5. **Enable Websockets Support**: ✓ (Important for SignalR!)

### Additional headers (Custom Nginx Configuration):
```nginx
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Real-IP $remote_addr;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

# SignalR specific
proxy_set_header Connection $http_connection;
proxy_http_version 1.1;
proxy_cache_bypass $http_upgrade;
```

## Step 5: SSL Certificate

1. In Nginx Proxy Manager, go to SSL tab
2. Request a new SSL certificate using Let's Encrypt
3. Enable "Force SSL"

## Updating the Application

To update to the latest version:

```bash
# Pull latest images
docker-compose -f docker-compose.prod.yml pull

# Restart containers
docker-compose -f docker-compose.prod.yml up -d
```

## Monitoring

View logs:
```bash
# All services
docker-compose -f docker-compose.prod.yml logs -f

# Specific service
docker-compose -f docker-compose.prod.yml logs -f blazorchat-server
```

## Backup

Important data to backup:
- `./uploads/` directory (uploaded images)

## Troubleshooting

### Container can't start
```bash
docker-compose -f docker-compose.prod.yml down
docker-compose -f docker-compose.prod.yml up -d
```

### Permission issues with uploads
```bash
chmod -R 777 uploads/
```

### SignalR connection issues
- Ensure WebSockets are enabled in Nginx Proxy Manager
- Check that both containers are on the same network
- Verify the ApiUrl environment variable is correct