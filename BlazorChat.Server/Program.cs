using BlazorChat.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();

// Add chat history service as singleton
builder.Services.AddSingleton<BlazorChat.Server.Services.ChatHistoryService>();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin => true) // Allow any origin
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseCors();

// Serve static files (for uploaded images)
app.UseStaticFiles();

app.MapGet("/", () => "Welcome to the Blazor Chat Server!");

app.MapControllers();
app.MapHub<ChatHub>("/api/chathub");

app.Run();
