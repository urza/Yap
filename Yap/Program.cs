using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.FileProviders;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Stores;
using Yap.Components;
using Yap.Extensions;
using Yap.Helpers;
using Yap.Middleware;
using Yap.Models;
using Yap.Services;

var builder = WebApplication.CreateBuilder(args);

// No framework-level size limits — actual limit enforced in upload endpoint via ChatSettings:MaxUploadSizeMB
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = null;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
});

// Load config from Data folder if exists (for Docker deployment)
// Replaces the default appsettings.json entirely to avoid .NET's array merge behavior
var dataConfigPath = Path.Combine(builder.Environment.ContentRootPath, "Data", "appsettings.json");
var dataDir = Path.GetDirectoryName(dataConfigPath)!;
if (Directory.Exists(dataDir) && !File.Exists(dataConfigPath))
{
    // Seed the Data folder with default config so deployers have a template to edit
    var defaultPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
    if (File.Exists(defaultPath))
        File.Copy(defaultPath, dataConfigPath);
}
if (File.Exists(dataConfigPath))
{
    var defaultSource = builder.Configuration.Sources
        .OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>()
        .FirstOrDefault(s => s.Path == "appsettings.json");
    if (defaultSource != null)
        builder.Configuration.Sources.Remove(defaultSource);

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
builder.Services.AddSingleton<GeoLocationService>();
builder.Services.AddSingleton<CustomEmojiService>();
builder.Services.AddSingleton<ImageService>();
builder.Services.AddSingleton<VideoService>();
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushNotificationService>();
builder.Services.AddSingleton<CircuitTracker>();  // Circuit diagnostics
builder.Services.AddSingleton<UserService>();     // User management with token auth
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<SystemBotService>();
builder.Services.AddSingleton<RegistrationGateService>();
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

// Media upload logging (queued, flushed to database periodically)
builder.Services.AddSingleton<MediaUploadLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaUploadLogService>());

// CORS for upload subdomain (tus uploads may come from a different origin)
builder.Services.AddCors(options =>
{
    options.AddPolicy("TusUpload", policy =>
    {
        policy.SetIsOriginAllowed(_ => true) // Allow any origin (app serves both domains)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .WithExposedHeaders("Upload-Offset", "Upload-Length", "Tus-Resumable", "Location");
    });
});

var app = builder.Build();

// Initialize persistence (migrations + load data) if enabled
await app.Services.InitializePersistenceAsync();

// Initialize users from database (must be before ChatService.InitializeAsync)
await app.Services.GetRequiredService<UserService>().LoadUsersAsync();

// Load push subscriptions from database
await app.Services.GetRequiredService<PushSubscriptionStore>().InitializeAsync();

// Initialize system bot (must be after UserService + ChatService)
await app.Services.GetRequiredService<SystemBotService>().InitializeAsync();

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
app.UseCors();
app.UseHttpsRedirection();
app.UseAntiforgery();

// Serve custom branding overrides from Data/branding/ (favicon, PWA icons, manifest, etc.)
// Files here override same-named files from wwwroot — no rebuild needed.
// Uses raw middleware to guarantee priority over MapStaticAssets in production.
var brandingPath = Path.Combine(app.Environment.ContentRootPath, "Data", "branding");
Directory.CreateDirectory(brandingPath);
var brandingProvider = new PhysicalFileProvider(brandingPath);
var brandingContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.Use(async (context, next) =>
{
    var fileInfo = brandingProvider.GetFileInfo(context.Request.Path.Value ?? "");
    if (fileInfo.Exists && !fileInfo.IsDirectory && fileInfo.PhysicalPath != null
        && brandingContentTypes.TryGetContentType(fileInfo.Name, out var contentType))
    {
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;
        await context.Response.SendFileAsync(fileInfo.PhysicalPath);
        return;
    }
    await next();
});

app.UseStaticFiles();

// Serve custom emojis from Data/custom-emojis/ folder
var customEmojisPath = Path.Combine(app.Environment.ContentRootPath, "Data", "custom-emojis");
Directory.CreateDirectory(customEmojisPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(customEmojisPath),
    RequestPath = "/custom-emojis"
});

// Serve welcome page assets from Data/welcome/ folder
var welcomePath = Path.Combine(app.Environment.ContentRootPath, "Data", "welcome");
Directory.CreateDirectory(welcomePath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(welcomePath),
    RequestPath = "/welcome-content"
});

// Custom middlewares - positioned after UseStaticFiles() to skip static file requests
app.UseMiddleware<AuthMiddleware>();
app.UseMiddleware<DeviceDetectionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// =============================================================================
// RESUMABLE FILE UPLOAD (tus.io protocol)
// =============================================================================
// Completed upload results stored here, keyed by tus file ID.
// JS fetches result via /api/tus/info/{fileId} after upload completes.
var tusCompletedFiles = new ConcurrentDictionary<string, object>();

var tusStorePath = Path.Combine(app.Environment.WebRootPath, "uploads", "tus-temp");
Directory.CreateDirectory(tusStorePath);

var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

app.MapTus("/api/tus", async httpContext => new()
{
    Store = new TusDiskStore(tusStorePath),
    MaxAllowedUploadSizeInBytesLong = (long)httpContext.RequestServices.GetRequiredService<IConfiguration>()
        .GetValue<int>("ChatSettings:MaxUploadSizeMB", 100) * 1024 * 1024,
    Events = new()
    {
        OnAuthorizeAsync = eventContext =>
        {
            // Allow OPTIONS (CORS preflight) without auth
            if (eventContext.HttpContext.Request.Method == "OPTIONS")
                return Task.CompletedTask;

            var token = eventContext.HttpContext.Request.Cookies[AuthMiddleware.CookieName];
            var userService = eventContext.HttpContext.RequestServices.GetRequiredService<UserService>();
            var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
            if (user == null)
            {
                eventContext.FailRequest(HttpStatusCode.Unauthorized, "Authentication required");
            }
            return Task.CompletedTask;
        },

        OnFileCompleteAsync = async eventContext =>
        {
            var logger = eventContext.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TusUpload");
            var imageService = eventContext.HttpContext.RequestServices.GetRequiredService<ImageService>();
            var videoService = eventContext.HttpContext.RequestServices.GetRequiredService<VideoService>();
            var mediaLog = eventContext.HttpContext.RequestServices.GetRequiredService<MediaUploadLogService>();
            var userService = eventContext.HttpContext.RequestServices.GetRequiredService<UserService>();

            var file = await eventContext.GetFileAsync();
            var metadata = await file.GetMetadataAsync(eventContext.CancellationToken);

            // Extract original filename from metadata
            var originalFileName = metadata.TryGetValue("filename", out var fName)
                ? fName.GetString(System.Text.Encoding.UTF8) : "unknown";
            var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

            // Determine file type
            string type;
            if (imageExtensions.Contains(extension))
                type = "image";
            else if (VideoService.IsVideoFile(extension))
                type = "video";
            else
            {
                logger.LogWarning("Unsupported file type uploaded via tus: {Extension}", extension);
                return;
            }

            // Move from tus temp store to uploads folder
            var uploadsFolder = Path.Combine(app.Environment.WebRootPath, "uploads");
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var filePath = Path.Combine(uploadsFolder, uniqueFileName);
            var tusFilePath = Path.Combine(tusStorePath, file.Id);

            File.Move(tusFilePath, filePath);
            // Clean up tus metadata files
            foreach (var metaFile in Directory.GetFiles(tusStorePath, $"{file.Id}.*"))
                try { File.Delete(metaFile); } catch { }

            var fileSize = new FileInfo(filePath).Length;
            logger.LogDebug("Tus upload complete: {FileName} ({Type}, {Size}KB)", uniqueFileName, type, fileSize / 1024);

            // Resolve user for logging
            var token = eventContext.HttpContext.Request.Cookies[AuthMiddleware.CookieName];
            var user = !string.IsNullOrEmpty(token) ? userService.AuthenticateByToken(token) : null;
            if (user != null)
            {
                mediaLog.Log(user.Id, user.Username, originalFileName, uniqueFileName, fileSize, type, extension);
            }

            // Blocking: generate medium thumbnail (images) or poster (videos)
            if (type == "image")
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await imageService.GenerateMediumThumbnailAsync(filePath);
                var mediumMs = sw.ElapsedMilliseconds;

                // Background: large thumbnail + update processing time
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var swLarge = System.Diagnostics.Stopwatch.StartNew();
                        await imageService.GenerateLargeThumbnailAsync(filePath);
                        swLarge.Stop();
                        await mediaLog.SetCompressDurationAsync(uniqueFileName, mediumMs + swLarge.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error generating large thumbnail for {FileName}", uniqueFileName);
                    }
                });
            }
            else // video
            {
                if (VideoService.IsAvailable)
                {
                    await videoService.GeneratePosterAsync(filePath);

                    // Background: compress video
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (compressedPath, durationMs) = await videoService.CompressVideoAsync(filePath);
                            if (compressedPath != null && durationMs > 0)
                                await mediaLog.SetCompressDurationAsync(uniqueFileName, durationMs);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error compressing video {FileName}", uniqueFileName);
                        }
                    });
                }
            }

            // Store result for JS to fetch
            tusCompletedFiles[file.Id] = new { url = $"/uploads/{uniqueFileName}", path = filePath, type };
        }
    }
}).RequireCors("TusUpload");

// Endpoint for JS to fetch completed file info after tus upload
app.MapGet("/api/tus/info/{fileId}", (string fileId) =>
{
    if (tusCompletedFiles.TryRemove(fileId, out var result))
        return Results.Ok(result);
    return Results.NotFound(new { error = "File not found or still processing" });
}).RequireCors("TusUpload");

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

// GET: new user signup (from Login.razor)
app.MapGet("/auth/signin", async (HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService, string username, string? password, string? returnUrl) =>
    await HandleSignIn(context, userService, actionLog, botService, username, password, returnUrl));

// POST: existing user with passphrase (from VerifyDevice.razor — avoids password in URL)
app.MapPost("/auth/signin", async (HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService) =>
{
    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();
    return await HandleSignIn(context, userService, actionLog, botService, username, password, returnUrl);
}).DisableAntiforgery();

async Task<IResult> HandleSignIn(HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService, string username, string? password, string? returnUrl)
{
    if (string.IsNullOrEmpty(username))
        return Results.Redirect("/");

    // Block login as bot user
    if (botService.IsBotUser(username))
        return Results.Redirect("/login");

    var registrationGate = context.RequestServices.GetRequiredService<RegistrationGateService>();

    User? user;
    string? newDeviceMethod = null; // set when this is a returning-user login from a new device

    if (!string.IsNullOrEmpty(password))
    {
        // Existing user with password — verify credentials
        user = userService.VerifyPassword(username, password);
        if (user == null)
            return Results.Redirect("/");
        newDeviceMethod = "passphrase";
    }
    else
    {
        // Check if username already exists
        var existingUser = userService.GetByUsername(username);
        if (existingUser != null)
        {
            // Smart mode: auto-login if same IP as an active session (unless user opted out)
            if (registrationGate.SmartMode && !existingUser.SmartLoginOptOut)
            {
                var chatService = context.RequestServices.GetRequiredService<ChatService>();
                var requestIp = IpHelper.GetClientIp(context);
                if (chatService.HasActiveSessionFromIp(username, requestIp))
                {
                    user = existingUser;
                    newDeviceMethod = "smart";
                    actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.SMART_LOGIN,
                        info: username, ip: requestIp ?? "unknown");
                }
                else
                {
                    return Results.Redirect("/login");
                }
            }
            else
            {
                // Existing user, no smart mode — can't create duplicate
                return Results.Redirect("/login");
            }
        }
        else
        {
            // Safety net: block new user creation if registration is closed
            if (registrationGate.RegistrationClosed)
                return Results.Redirect("/login");

            // Safety net: if approval required, only allow approved users through
            if (registrationGate.RequireApproval && !registrationGate.ConsumeApproval(username))
                return Results.Redirect("/login");

            // New user — create account
            user = await userService.CreateUserAsync(username);
            if (user == null)
                return Results.Redirect("/");
        }
    }

    AuthMiddleware.SetAuthCookie(context, user.Token);

    var ip = IpHelper.GetClientIp(context) ?? "unknown";
    var ua = context.Request.Headers.UserAgent.ToString();
    actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: username, ip: ip, userAgent: ua);

    // Notify user of new device login via bot DM (fire-and-forget)
    if (newDeviceMethod != null)
        _ = botService.NotifyNewDeviceLoginAsync(username, newDeviceMethod, ip);

    // Validate returnUrl is relative (prevent open redirect)
    var destination = "/lobby";
    if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
    {
        destination = returnUrl;
    }

    return Results.Redirect(destination);
}

app.MapGet("/auth/signout", (HttpContext context, UserService userService, UserActionLogService actionLog) =>
{
    // Try to identify the user before clearing cookie (may be null if already deleted by ChatHeader.SignOut)
    var token = context.Request.Cookies[AuthMiddleware.CookieName];
    if (!string.IsNullOrEmpty(token))
    {
        var user = userService.AuthenticateByToken(token);
        if (user != null)
        {
            var ip = IpHelper.GetClientIp(context) ?? "unknown";
            actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGOUT, info: user.Username, ip: ip);
        }
    }

    AuthMiddleware.ClearAuthCookie(context);
    return Results.Redirect("/");
});

app.MapGet("/auth/refresh-token", (HttpContext context, UserService userService, string token, string? returnUrl) =>
{
    // Validate token exists — only set cookie if it maps to a real user
    var user = userService.AuthenticateByToken(token);
    if (user == null)
        return Results.Redirect("/");

    AuthMiddleware.SetAuthCookie(context, token);

    var destination = "/settings";
    if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
        destination = returnUrl;

    return Results.Redirect(destination);
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

// Request DTOs for push API
record PushSubscribeRequest(string Username, string Endpoint, string? P256dh, string? Auth);
record PushUnsubscribeRequest(string Endpoint);
