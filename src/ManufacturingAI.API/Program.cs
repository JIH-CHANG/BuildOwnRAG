using FluentValidation;
using Hangfire;
using ManufacturingAI.API;
using ManufacturingAI.API.Auth;
using ManufacturingAI.API.Middleware;
using ManufacturingAI.API.Workers;
using ManufacturingAI.Connectors.Confluence;
using ManufacturingAI.Connectors.Folder;
using ManufacturingAI.Connectors.GoogleDrive;
using ManufacturingAI.Connectors.SharePoint;
using ManufacturingAI.Core.Configuration;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Core.RAG;
using ManufacturingAI.Infrastructure;
using ManufacturingAI.Services.Analytics;
using ManufacturingAI.Services.Ingest;
using ManufacturingAI.Services.Query;
using ManufacturingAI.Services.TestGen;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using System.Text;
using System.Threading.RateLimiting;

// UseSerilog() below replaces the whole ILoggerFactory rather than adding one more
// provider, so OpenTelemetry's own logging bridge (AddOpenTelemetry().WithLogging(...))
// never sees a single log record. Serilog has to export OTLP itself.
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

var loggerConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    // HttpClient logs the full request URI at Information level — for Gemini that URI contains
    // ?key=<apiKey>. Raise to Warning so provider API keys never land in logs.
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console();

if (!string.IsNullOrEmpty(otlpEndpoint))
{
    loggerConfig = loggerConfig.WriteTo.OpenTelemetry(options =>
    {
        options.Endpoint = otlpEndpoint;
        options.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
        options.ResourceAttributes = new Dictionary<string, object>
        {
            ["service.name"] = "BuildOwnRAG.API"
        };
    });
}

Log.Logger = loggerConfig.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    var config = builder.Configuration;
    var jwtSecret = config["Jwt:Secret"] ?? throw new InvalidOperationException("Jwt:Secret not configured.");

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("BuildOwnRAG.API"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            // Outgoing HTTP = LLM/embedding provider calls. Gemini carries the API
            // key in the query string, so record the URL without query at all —
            // stronger guarantee than the library's default value redaction.
            .AddHttpClientInstrumentation(o =>
                o.EnrichWithHttpRequestMessage = (activity, request) =>
                {
                    if (request.RequestUri is { } uri)
                        activity.SetTag("url.full", uri.GetLeftPart(UriPartial.Path));
                })
            .AddSource("Npgsql")
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Npgsql")
            .AddMeter(ManufacturingAI.Core.Observability.AppMetrics.MeterName)
            .AddOtlpExporter());
        // Logs are exported by Serilog directly (see the WriteTo.OpenTelemetry sink
        // above) — Serilog owns the whole ILoggerFactory, so there is no separate
        // .WithLogging() bridge to configure here.

    builder.Services.AddInfrastructure(config);
    builder.Services.AddCoreRAG(config);
    builder.Services.AddIngestServices(config);
    builder.Services.AddAnalyticsServices();
    builder.Services.AddTestGenServices();
    builder.Services.AddScoped<ITokenService, TokenService>();

    // Retrieval pipelines: QueryRouter picks Hybrid (QueryService) or Lite/BM25 per tenant setting.
    builder.Services.AddSingleton(
        config.GetSection("LiteMode").Get<LiteModeOptions>() ?? new LiteModeOptions());
    builder.Services.AddScoped<QueryService>();
    builder.Services.AddScoped<ILiteQueryService, LiteQueryService>();
    builder.Services.AddScoped<IQueryService, QueryRouter>();
    builder.Services.AddValidatorsFromAssemblyContaining<Program>();

    // Ingest worker (WORKER_ENABLED env var controls activation inside the worker)
    builder.Services.AddHostedService<IngestWorker>();

    // Connector implementations
    builder.Services.AddFolderConnector();
    builder.Services.AddGoogleDriveConnector();
    builder.Services.AddSharePointConnector();
    builder.Services.AddConfluenceConnector();

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.MapInboundClaims = false; // keep raw claim names (role, userId, tenantId) as issued
        options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ValidateIssuer = true,
                ValidIssuer = config["Jwt:Issuer"] ?? "BuildOwnRAG",
                ValidateAudience = true,
                ValidAudience = config["Jwt:Audience"] ?? "BuildOwnRAG",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
        })
        .AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(
            ApiKeyAuthHandler.SchemeName, _ => { });

    builder.Services.AddAuthorization(options =>
    {
        var canQuery = new[] { nameof(UserRole.Operator), nameof(UserRole.Engineer), nameof(UserRole.TenantAdmin) };
        var canIngest = new[] { nameof(UserRole.Engineer), nameof(UserRole.TenantAdmin) };
        var canViewIngest = new[] { nameof(UserRole.Operator), nameof(UserRole.Engineer), nameof(UserRole.TenantAdmin) };
        var adminOnly = new[] { nameof(UserRole.TenantAdmin) };
        var canView = new[] { nameof(UserRole.Engineer), nameof(UserRole.TenantAdmin), nameof(UserRole.Viewer) };
        options.AddPolicy("CanQuery", p => p.RequireClaim("role", canQuery));
        options.AddPolicy("CanIngest", p => p.RequireClaim("role", canIngest));
        options.AddPolicy("CanViewIngest", p => p.RequireClaim("role", canViewIngest));
        options.AddPolicy("CanManageConnectors", p => p.RequireClaim("role", adminOnly));
        options.AddPolicy("CanManageUsers", p => p.RequireClaim("role", adminOnly));
        options.AddPolicy("CanViewAnalytics", p => p.RequireClaim("role", canView));
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.AddPolicy("PerIpPerMinute", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0
                }));
        options.RejectionStatusCode = 429;
    });

    var allowedOrigins = config["AllowedOrigins"];
    builder.Services.AddCors(options =>
        options.AddDefaultPolicy(policy =>
        {
            if (string.IsNullOrWhiteSpace(allowedOrigins) || allowedOrigins == "*")
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            }
            else
            {
                var origins = allowedOrigins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
            }
        }));

    builder.Services.AddHttpClient();
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "BuildOwnRAG API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new()
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header
        });
        c.AddSecurityRequirement(new()
        {
            {
                new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
                Array.Empty<string>()
            }
        });
    });
    var app = builder.Build();

    // Middleware order follows spec: Exception → OTel(service) → Serilog → HTTPS → Auth → Authz → RateLimit → CORS → Controllers
    app.UseMiddleware<GlobalExceptionMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseSwagger();
    app.UseSwaggerUI();

    // Skip HTTPS redirect when running inside Docker (nginx handles TLS termination)
    if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") != "true")
        app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    app.UseCors();

    if (app.Environment.IsDevelopment())
        app.UseHangfireDashboard("/hangfire");

    app.MapControllers().RequireRateLimiting("PerIpPerMinute");

    await StartupInitializer.RunAsync(app);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed.");
}
finally
{
    Log.CloseAndFlush();
}
