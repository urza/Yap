using BlazorChat.Client.Pages;
using BlazorChat.Client.Serve.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Configuration discovery endpoint - WebAssembly fetches API server URL from here
// This solves the problem that WebAssembly (running in browser) needs to know 
// where the API server is, but can't have hardcoded URLs since they change per environment
app.MapGet("/api/config", (IConfiguration config) => new
{
    ApiUrl = config["ApiUrl"] ?? "http://localhost:5224"
});

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(BlazorChat.Client._Imports).Assembly);

app.Run();
