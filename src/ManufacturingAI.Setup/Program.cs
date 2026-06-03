using ManufacturingAI.Setup;
using ManufacturingAI.Setup.Controllers;

// CLI auto-confirm mode — headless install from environment variables
if (Environment.GetEnvironmentVariable("SETUP_AUTO_CONFIRM") == "true")
    return await AutoSetup.RunAsync();

// Web UI mode
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://+:8081");

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<SetupService>();
builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

Console.WriteLine("[BuildOwnRAG Setup] Web UI ready → http://localhost:8081");
await app.RunAsync();
return 0;
