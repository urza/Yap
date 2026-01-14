using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Yap.Components;
using Yap.Extensions;
using Yap.Services;

var builder = WebApplication.CreateBuilder(args);

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

// Chat services
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<PushNotificationService>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddScoped<ChatConfigService>();
builder.Services.AddScoped<EmojiService>();
builder.Services.AddScoped<UserStateService>();
builder.Services.AddScoped<ChatNavigationState>();
builder.Services.AddScoped<CircuitHandler, ChatCircuitHandler>();

var app = builder.Build();

// Initialize persistence (migrations + load data) if enabled
await app.Services.InitializePersistenceAsync();

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
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// =============================================================================
// PUSH NOTIFICATION API ENDPOINTS
// =============================================================================
app.MapGet("/api/push/vapid-public-key", (PushNotificationService pushService) =>
{
    var publicKey = pushService.GetPublicKey();
    return publicKey != null
        ? Results.Ok(new { publicKey })
        : Results.NotFound(new { error = "VAPID not configured" });
});

app.MapPost("/api/push/subscribe", async (HttpContext context, PushSubscriptionStore store) =>
{
    var body = await context.Request.ReadFromJsonAsync<PushSubscribeRequest>();
    if (body == null || string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Endpoint))
        return Results.BadRequest(new { error = "Invalid subscription" });

    store.SaveSubscription(body.Username, new PushSubscriptionInfo
    {
        Endpoint = body.Endpoint,
        P256dh = body.P256dh ?? "",
        Auth = body.Auth ?? ""
    });

    return Results.Ok(new { success = true });
});

app.MapPost("/api/push/unsubscribe", async (HttpContext context, PushSubscriptionStore store) =>
{
    var body = await context.Request.ReadFromJsonAsync<PushUnsubscribeRequest>();
    if (body == null || string.IsNullOrEmpty(body.Endpoint))
        return Results.BadRequest(new { error = "Invalid request" });

    store.RemoveSubscription(body.Endpoint);
    return Results.Ok(new { success = true });
});

app.Run();

// Request DTOs for push API
record PushSubscribeRequest(string Username, string Endpoint, string? P256dh, string? Auth);
record PushUnsubscribeRequest(string Endpoint);
