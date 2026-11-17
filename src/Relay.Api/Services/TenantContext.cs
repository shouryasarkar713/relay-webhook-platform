using System.Security.Claims;
using Relay.Api.Authentication;
using Relay.Application.Abstractions;

namespace Relay.Api.Services;

public sealed class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid TenantId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            var claim = user?.FindFirst(ApiKeyAuthenticationHandler.TenantIdClaimType);
            return claim is not null && Guid.TryParse(claim.Value, out var tenantId)
                ? tenantId
                : Guid.Empty;
        }
    }

    public Guid ApiKeyId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            var claim = user?.FindFirst(ApiKeyAuthenticationHandler.ApiKeyIdClaimType);
            return claim is not null && Guid.TryParse(claim.Value, out var apiKeyId)
                ? apiKeyId
                : Guid.Empty;
        }
    }

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true &&
        TenantId != Guid.Empty;
}
