using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Relay.Application.Abstractions;

namespace Relay.Api.Authentication;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>
{
    public const string TenantIdClaimType = "relay:tenant_id";
    public const string ApiKeyIdClaimType = "relay:api_key_id";
    public const string EnvironmentClaimType = "relay:environment";

    private readonly IRelayDbContext _dbContext;
    private readonly IApiKeyHasher _apiKeyHasher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApiKeyAuthenticationHandler> _logger;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IRelayDbContext dbContext,
        IApiKeyHasher apiKeyHasher,
        TimeProvider timeProvider)
        : base(options, loggerFactory, encoder)
    {
        _dbContext = dbContext;
        _apiKeyHasher = apiKeyHasher;
        _timeProvider = timeProvider;
        _logger = loggerFactory.CreateLogger<ApiKeyAuthenticationHandler>();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationSchemeOptions.HeaderName, out var headerValues) ||
            string.IsNullOrWhiteSpace(headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawApiKey = headerValues.ToString();

        if (!_apiKeyHasher.TryParseKey(rawApiKey, out var parts))
        {
            _logger.LogWarning("Authentication failed: Malformed API key format.");
            return AuthenticateResult.Fail("Invalid API key format.");
        }

        // Fast prefix lookup using unique index
        var apiKey = await _dbContext.ApiKeys
            .Include(k => k.Tenant)
            .FirstOrDefaultAsync(k => k.KeyPrefix == parts.Prefix, Context.RequestAborted);

        if (apiKey is null)
        {
            _logger.LogWarning("Authentication failed: API key with prefix {Prefix} not found.", parts.Prefix);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var now = _timeProvider.GetUtcNow();

        if (!apiKey.IsValid(now))
        {
            _logger.LogWarning("Authentication failed: API key {ApiKeyId} is revoked or expired.", apiKey.Id);
            return AuthenticateResult.Fail("API key is revoked or expired.");
        }

        if (apiKey.Tenant is null || !apiKey.Tenant.IsActive)
        {
            _logger.LogWarning("Authentication failed: Tenant for API key {ApiKeyId} is inactive or missing.", apiKey.Id);
            return AuthenticateResult.Fail("Tenant is inactive.");
        }

        // Constant-time hash verification
        if (!_apiKeyHasher.VerifySecret(parts.Secret, apiKey.KeyHash))
        {
            _logger.LogWarning("Authentication failed: Secret verification mismatch for prefix {Prefix}.", parts.Prefix);
            return AuthenticateResult.Fail("Invalid API key secret.");
        }

        // Record usage timestamp
        apiKey.RecordUsage(now);
        try
        {
            await _dbContext.SaveChangesAsync(Context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastUsedAtUtc timestamp for key {ApiKeyId}", apiKey.Id);
            // Non-fatal: do not block authentication if updating timestamp fails
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.Id.ToString()),
            new Claim(ClaimTypes.Name, apiKey.Name),
            new Claim(TenantIdClaimType, apiKey.TenantId.ToString()),
            new Claim(ApiKeyIdClaimType, apiKey.Id.ToString()),
            new Claim(EnvironmentClaimType, parts.Environment)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
