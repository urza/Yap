using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using BlazorChat.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Configure HttpClient
builder.Services.AddScoped(sp => 
{
    var httpClient = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    return httpClient;
});

// Add configuration service
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

// Register ChatConfigService
builder.Services.AddScoped<ChatConfigService>();

// Register EmojiService
builder.Services.AddScoped<EmojiService>();

await builder.Build().RunAsync();
