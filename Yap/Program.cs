using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.FileProviders;
using Yap.Components;
using Yap.Extensions;
using Yap.Middleware;
using Yap.Models;
using Yap.Services;

var builder = WebApplication.CreateBuilder(args);

// Allow large file uploads (100 MB) - matches app-level limit
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

// Load config from Data folder if exists (for Docker deployment)
var dataConfigPath = Path.Combine(builder.Environment.ContentRootPath, "Data", "appsettings.json");
if (File.Exists(dataConfigPath))
{
    builder.Configuration.AddJsonFile(dataConfigPath, optional: false, reloadOnChange: true);
}

// =============================================================================
// BLAZOR SERVER CIRCUIT CONFIGURATION (.NET 10)
// =============================================================================
// Blazor Server uses "circuits" to maintain state for each connected user.
// When the WebSocket connection drops (e.g., user closes laptop), the circuit
// becomes "disconnected" but is kept alive for a grace period.
//
// There are TWO retention periods to understand:
// 1. DisconnectedCircuitRetentionPeriod - How long the circuit stays "warm"
//    waiting for the SAME WebSocket to reconnect (default: 3 minutes)
// 2. PersistedCircuitInMemoryRetentionPeriod - How long the STATE is kept
//    AFTER the circuit is evicted, allowing a NEW circuit to restore it
//    via Blazor.resumeCircuit() (default: 2 hours)
// =============================================================================

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Keep disconnected circuits alive for 4 hours instead of 3 minutes.
        // This allows seamless reconnection if user returns within 4 hours
        // (e.g., laptop sleep, switching apps on phone).
        // Tradeoff: Each retained circuit uses server memory.
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(4);
        //for debugging evictions, set a short time:
        //options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(10);

        // Maximum number of disconnected circuits to retain (default: 100).
        // Increase this if you expect many concurrent users going idle.
        options.DisconnectedCircuitMaxRetained = 1000;

        // Show detailed errors in browser console (useful for debugging)
        options.DetailedErrors = builder.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("DetailedErrors");
    })
    // =============================================================================
    // PERSISTENT SERVICES (.NET 10)
    // =============================================================================
    // RegisterPersistentService tells Blazor to automatically persist properties
    // marked with [PersistentState] when a circuit is evicted. When the user
    // reconnects and calls Blazor.resumeCircuit(), these properties are restored.
    // This allows users to "resume" their session even after long disconnections.
    // =============================================================================
    .RegisterPersistentService<UserStateService>(RenderMode.InteractiveServer)
    .RegisterPersistentService<ChatNavigationState>(RenderMode.InteractiveServer);

// =============================================================================
// PERSISTED CIRCUIT STATE RETENTION (.NET 10)
// =============================================================================
// After a circuit is evicted (disconnected longer than DisconnectedCircuitRetentionPeriod),
// the [PersistentState] data is still kept in memory for this duration.
// This is separate from the circuit itself - it's just the serialized state.
// =============================================================================
builder.Services.Configure<CircuitOptions>(options =>
{
    // Keep persisted state for 24 hours (default: 2 hours).
    // This means a user can close their browser, come back the next day,
    // and still have their username/navigation state restored.
    options.PersistedCircuitInMemoryRetentionPeriod = TimeSpan.FromHours(48);

    // Maximum number of persisted states to keep (default: 1000).
    options.PersistedCircuitInMemoryMaxRetained = 5000;
});

// Persistence (optional database support)
builder.Services.AddChatPersistence(builder.Configuration);

// Push subscription persistence (Json file or Database)
var pushStorage = builder.Configuration.GetValue<string>("ChatSettings:PushSubscriptionStorage") ?? "Json";
if (pushStorage.Equals("Database", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IPushSubscriptionPersistence, DbPushSubscriptionPersistence>();
else
    builder.Services.AddSingleton<IPushSubscriptionPersistence, JsonPushSubscriptionPersistence>();

// Chat services
builder.Services.AddSingleton<CustomEmojiService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushNotificationService>();
builder.Services.AddSingleton<CircuitTracker>();  // Circuit diagnostics
builder.Services.AddSingleton<UserService>();     // User management with token auth
builder.Services.AddSingleton<ChatService>();
builder.Services.AddScoped<ChatConfigService>();
builder.Services.AddScoped<EmojiService>();
builder.Services.AddScoped<UserStateService>();
builder.Services.AddScoped<ChatNavigationState>();
builder.Services.AddScoped<CircuitHandler, ChatCircuitHandler>();

// HTTP context accessor (for device detection in Blazor components)
builder.Services.AddHttpContextAccessor();

// Request logging
builder.Services.AddSingleton<RequestLogQueue>();
builder.Services.AddHostedService<RequestLogWriter>();

// User action logging (queued, flushed to database periodically)
builder.Services.AddSingleton<UserActionLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UserActionLogService>());

var app = builder.Build();

// Initialize persistence (migrations + load data) if enabled
await app.Services.InitializePersistenceAsync();

// Initialize users from database (must be before ChatService.InitializeAsync)
await app.Services.GetRequiredService<UserService>().LoadUsersAsync();

// Load push subscriptions from database
await app.Services.GetRequiredService<PushSubscriptionStore>().InitializeAsync();

// Clean up old action logs (keep last 100 per user, delete older than 6 months)
await app.Services.GetRequiredService<UserActionLogService>().CleanupAsync();

// Clear uploads folder on start if configured
if (builder.Configuration.GetValue<bool>("ChatSettings:ClearUploadsOnStart", true))
{
    var uploadsPath = Path.Combine(app.Environment.WebRootPath, "uploads");
    if (Directory.Exists(uploadsPath))
    {
        foreach (var file in Directory.GetFiles(uploadsPath))
        {
            try { File.Delete(file); } catch { /* ignore errors */ }
        }
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseStaticFiles(); // Serve uploaded images from wwwroot/uploads

// Serve custom emojis from Data/custom-emojis/ folder
var customEmojisPath = Path.Combine(app.Environment.ContentRootPath, "Data", "custom-emojis");
Directory.CreateDirectory(customEmojisPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(customEmojisPath),
    RequestPath = "/custom-emojis"
});

// Custom middlewares - positioned after UseStaticFiles() to skip static file requests
app.UseMiddleware<AuthMiddleware>();
app.UseMiddleware<DeviceDetectionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// =============================================================================
// FILE UPLOAD ENDPOINT (for parallel HTTP uploads)
// =============================================================================
app.MapPost("/api/upload", async (IFormFile file, IWebHostEnvironment env, ILogger<Program> logger) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided" });

    if (file.Length > 100 * 1024 * 1024)
        return Results.BadRequest(new { error = "File too large" });

    var allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!allowedExtensions.Contains(extension))
        return Results.BadRequest(new { error = "Invalid file type" });

    var uploadsFolder = Path.Combine(env.WebRootPath, "uploads");
    Directory.CreateDirectory(uploadsFolder);

    var uniqueFileName = $"{Guid.NewGuid()}{extension}";
    var filePath = Path.Combine(uploadsFolder, uniqueFileName);

    await using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    logger.LogDebug("File uploaded via HTTP: {FileName}", uniqueFileName);

    return Results.Ok(new { url = $"/uploads/{uniqueFileName}", path = filePath });
}).DisableAntiforgery();

// =============================================================================
// PUSH NOTIFICATION API ENDPOINTS
// =============================================================================
app.MapGet("/api/push/vapid-public-key", (PushNotificationService pushService, ILogger<Program> logger) =>
{
    var publicKey = pushService.GetPublicKey();
    logger.LogDebug("VAPID public key requested, configured={IsConfigured}", publicKey != null);
    return publicKey != null
        ? Results.Ok(new { publicKey })
        : Results.NotFound(new { error = "VAPID not configured" });
});

app.MapPost("/api/push/subscribe", async (HttpContext context, PushSubscriptionStore store, UserService userService, ILogger<Program> logger) =>
{
    // Authenticate via cookie — only the logged-in user can subscribe themselves
    var token = context.Request.Cookies[AuthMiddleware.CookieName];
    var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
    if (user == null)
    {
        logger.LogDebug("Push subscribe rejected: no valid auth cookie");
        return Results.Unauthorized();
    }

    var body = await context.Request.ReadFromJsonAsync<PushSubscribeRequest>();
    if (body == null || string.IsNullOrEmpty(body.Endpoint))
        return Results.BadRequest(new { error = "Invalid subscription" });

    // Ignore the username from the body — use the authenticated user
    logger.LogDebug("Push subscribe for {Username}, endpoint={Endpoint}", user.Username, body.Endpoint[..Math.Min(50, body.Endpoint.Length)]);

    await store.SaveSubscriptionAsync(user.Username, new PushSubscriptionInfo
    {
        Endpoint = body.Endpoint,
        P256dh = body.P256dh ?? "",
        Auth = body.Auth ?? ""
    });

    return Results.Ok(new { success = true });
});

app.MapPost("/api/push/unsubscribe", async (HttpContext context, PushSubscriptionStore store, UserService userService, ILogger<Program> logger) =>
{
    // Authenticate via cookie
    var token = context.Request.Cookies[AuthMiddleware.CookieName];
    var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
    if (user == null)
    {
        logger.LogDebug("Push unsubscribe rejected: no valid auth cookie");
        return Results.Unauthorized();
    }

    var body = await context.Request.ReadFromJsonAsync<PushUnsubscribeRequest>();
    if (body == null || string.IsNullOrEmpty(body.Endpoint))
        return Results.BadRequest(new { error = "Invalid request" });

    logger.LogDebug("Push unsubscribe for {Username}, endpoint={Endpoint}", user.Username, body.Endpoint[..Math.Min(50, body.Endpoint.Length)]);

    await store.RemoveSubscriptionAsync(body.Endpoint);
    return Results.Ok(new { success = true });
});

// =============================================================================
// AUTH ROUTES
// =============================================================================
// HttpOnly cookies can only be set via HTTP response headers, not from Blazor
// after SignalR streaming starts. So we redirect here with forceLoad.

app.MapGet("/auth/signin", async (HttpContext context, UserService userService, UserActionLogService actionLog, string username, string? returnUrl) =>
{
    if (string.IsNullOrEmpty(username))
        return Results.Redirect("/");

    var user = await userService.CreateUserAsync(username);
    if (user == null)
        return Results.Redirect("/");

    AuthMiddleware.SetAuthCookie(context, user.Token);

    var ip = GetClientIp(context);
    var ua = context.Request.Headers.UserAgent.ToString();
    actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: username, ip: ip, userAgent: ua);

    // Validate returnUrl is relative (prevent open redirect)
    var destination = "/lobby";
    if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
    {
        destination = returnUrl;
    }

    return Results.Redirect(destination);
});

app.MapGet("/auth/signout", (HttpContext context, UserService userService, UserActionLogService actionLog) =>
{
    // Try to identify the user before clearing cookie (may be null if already deleted by ChatHeader.SignOut)
    var token = context.Request.Cookies[AuthMiddleware.CookieName];
    if (!string.IsNullOrEmpty(token))
    {
        var user = userService.AuthenticateByToken(token);
        if (user != null)
        {
            var ip = GetClientIp(context);
            actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGOUT, info: user.Username, ip: ip);
        }
    }

    AuthMiddleware.ClearAuthCookie(context);
    return Results.Redirect("/");
});

// =============================================================================
// ONE-TIME MIGRATION: Generate thumbnails for existing images
// Runs in background to avoid timeout - check console for progress
// =============================================================================
app.MapGet("/api/admin/generate-thumbnails", (ImageService imageService, IWebHostEnvironment env) =>
{
    var uploadsPath = Path.Combine(env.WebRootPath, "uploads");
    if (!Directory.Exists(uploadsPath))
        return Results.Ok(new { message = "No uploads folder" });

    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    // Find original images (exclude WebP thumbnails)
    var originalImages = Directory.GetFiles(uploadsPath)
        .Where(f => allowedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_800px"))
        .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_1600px"))
        .ToList();

    var toProcess = originalImages
        .Where(f => !File.Exists(Path.Combine(uploadsPath, $"{Path.GetFileNameWithoutExtension(f)}_800px.webp")))
        .ToList();

    // Run in background with parallel processing to avoid timeout
    _ = Task.Run(async () =>
    {
        var processed = 0;
        var failed = 0;

        await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            async (imagePath, _) =>
            {
                try
                {
                    await imageService.GenerateThumbnailsAsync(imagePath);
                    var count = Interlocked.Increment(ref processed);
                    Console.WriteLine($"[Thumbnails] {count}/{toProcess.Count}: {Path.GetFileName(imagePath)}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Console.WriteLine($"[Thumbnails] Failed: {Path.GetFileName(imagePath)} - {ex.Message}");
                }
            });

        Console.WriteLine($"[Thumbnails] Complete: {processed} processed, {failed} failed");
    });

    return Results.Ok(new { message = "Processing started in background", toProcess = toProcess.Count, total = originalImages.Count });
});

// =============================================================================
// DIAGNOSTICS ENDPOINT
// =============================================================================
app.MapGet("/api/diagnostics", (ChatService chatService, CircuitTracker circuitTracker) =>
{
    var diagnostics = chatService.GetDiagnostics();
    var (active, disconnected, totalCreated) = circuitTracker.GetStats();

    diagnostics.ActiveCircuits = active;
    diagnostics.DisconnectedCircuits = disconnected;
    diagnostics.TotalCircuitsCreated = totalCreated;

    return Results.Ok(diagnostics);
});

app.MapGet("/api/diagnostics/circuits", (CircuitTracker circuitTracker) =>
{
    var circuits = circuitTracker.GetAllCircuits();
    return Results.Ok(new
    {
        circuits = circuits.Select(c => new
        {
            c.CircuitId,
            c.CreatedAt,
            c.IsConnected,
            c.DisconnectedAt,
            AgeMinutes = (DateTime.UtcNow - c.CreatedAt).TotalMinutes
        }),
        summary = new
        {
            active = circuits.Count(c => c.IsConnected),
            disconnected = circuits.Count(c => !c.IsConnected)
        }
    });
});

// Test exception endpoint
app.MapGet("/api/test-exception", () =>
{
    throw new InvalidOperationException("This is a test exception to verify error handling!");
});

app.Run();

// Helper to extract client IP (same logic as RequestLoggingMiddleware)
static string GetClientIp(HttpContext context)
{
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(forwardedFor))
    {
        var firstIp = forwardedFor.Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(firstIp))
            return firstIp;
    }
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// Request DTOs for push API
record PushSubscribeRequest(string Username, string Endpoint, string? P256dh, string? Auth);
record PushUnsubscribeRequest(string Endpoint);
