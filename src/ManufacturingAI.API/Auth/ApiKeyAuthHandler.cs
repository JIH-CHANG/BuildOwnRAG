using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace ManufacturingAI.API.Auth;

public class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

public class ApiKeyAuthHandler(
    IOptionsMonitor<ApiKeyAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyRepository apiKeyRepository)
    : AuthenticationHandler<ApiKeyAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string HeaderName = "X-API-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var headerValue))
            return AuthenticateResult.NoResult();

        var keyHash = TokenService.HashToken(headerValue.ToString());
        var apiKey = await apiKeyRepository.GetByKeyHashAsync(keyHash, Context.RequestAborted);

        if (apiKey is null || !apiKey.IsActive)
            return AuthenticateResult.Fail("Invalid or inactive API key.");

        await apiKeyRepository.UpdateLastUsedAtAsync(apiKey.Id, Context.RequestAborted);

        var claims = new[]
        {
            new Claim("tenantId", apiKey.TenantId.ToString()),
            new Claim("role",     UserRole.TenantAdmin.ToString()),
            new Claim("keyId",    apiKey.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
