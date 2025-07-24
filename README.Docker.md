# BlazorChat Docker Deployment

## Quick Start

1. Build and run the containers:
```bash
docker-compose up --build
```

2. Access the application:
- Chat UI: http://localhost:5221
- SignalR Server: http://localhost:5224

## Architecture

The application consists of two containers:
- **blazorchat**: The Blazor WebAssembly host (port 5221)
- **blazorchat-server**: The SignalR hub and API server (port 5224)

## Configuration

### Environment Variables

You can override the API URL by setting environment variables:
```bash
docker-compose run -e ApiUrl=http://your-server:port blazorchat
```

### Persistent Storage

Uploaded images are stored in a Docker volume. To backup or restore uploads:
```bash
# Backup
docker cp blazorchat-server:/app/wwwroot/uploads ./backup

# Restore
docker cp ./backup/. blazorchat-server:/app/wwwroot/uploads/
```

## Production Deployment

For production, consider:
1. Using HTTPS (add SSL certificates)
2. Setting specific image tags instead of building
3. Using external volume storage for uploads
4. Adding health checks to docker-compose.yml