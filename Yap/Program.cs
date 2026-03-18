using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.FileProviders;
using Yap.Components;
using Yap.Endpoints;
using Yap.Extensions;
using Yap.Middleware;
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

// Diagnostics collector (captures snapshots every 30s for admin charts)
builder.Services.AddSingleton<DiagnosticsCollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiagnosticsCollectorService>());

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
// API ENDPOINTS (organized in Yap/Endpoints/)
// =============================================================================
app.MapTusEndpoints();
app.MapPushEndpoints();
app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapDiagnosticsEndpoints();

app.Run();
