using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Relay.Api.Authentication;
using Relay.Api.Middleware;
using Relay.Api.Services;
using Relay.Application;
using Relay.Application.Abstractions;
using Relay.Infrastructure;
using Relay.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Add application and infrastructure layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Add HTTP context accessor and tenant context
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Add controllers with JSON options
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

// Configure Authentication Scheme
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = ApiKeyAuthenticationSchemeOptions.SchemeName;
    options.DefaultChallengeScheme = ApiKeyAuthenticationSchemeOptions.SchemeName;
})
.AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationSchemeOptions.SchemeName,
    _ => { });

builder.Services.AddAuthorization();

// Configure CORS for React frontend
const string FrontendCorsPolicy = "FrontendCorsPolicy";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .WithHeaders("X-Api-Key", "Idempotency-Key", "Content-Type", "Accept")
            .WithMethods("GET", "POST", "OPTIONS")
            .WithExposedHeaders("Location");
    });
});

// Configure ASP.NET Core OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Optional development seed
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IApiKeyHasher>();
    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        if (dbContext.Database.CanConnect())
        {
            var (_, devKey) = await DbInitializer.EnsureDevelopmentTenantSeededAsync(
                dbContext, hasher, timeProvider, logger,
                specifiedDevSecret: "7f3a9e2b4c810d9e8f1234567890abcdef1234567890abcdef1234567890abcdef");

            if (devKey is not null)
            {
                logger.LogInformation("DEVELOPMENT API KEY: {DevKey}", devKey);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning("Could not seed development tenant at startup: {Message}", ex.Message);
    }
}

app.UseCors(FrontendCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program { }
