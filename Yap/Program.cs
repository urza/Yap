using Microsoft.AspNetCore.Components.Server.Circuits;
using Yap.Components;
using Yap.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chat services
builder.Services.AddSingleton<ChatService>();
builder.Services.AddScoped<ChatConfigService>();
builder.Services.AddScoped<EmojiService>();
builder.Services.AddScoped<UserStateService>();
builder.Services.AddScoped<CircuitHandler, ChatCircuitHandler>();

var app = builder.Build();

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

app.Run();
