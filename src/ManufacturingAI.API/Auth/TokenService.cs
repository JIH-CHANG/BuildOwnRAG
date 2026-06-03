using ManufacturingAI.Core.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ManufacturingAI.API.Auth;

public interface ITokenService
{
    string GenerateAccessToken(AppUser user);
    string GenerateRawRefreshToken();
}

public class TokenService(IConfiguration config) : ITokenService
{
    private readonly string _secret = config["Jwt:Secret"]
        ?? throw new InvalidOperationException("Jwt:Secret not configured.");
    private readonly string _issuer = config["Jwt:Issuer"] ?? "ManufacturingAI";
    private readonly string _audience = config["Jwt:Audience"] ?? "ManufacturingAI";

    public string GenerateAccessToken(AppUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("userId",   user.Id.ToString()),
            new Claim("tenantId", user.TenantId.ToString()),
            new Claim("role",     user.Role.ToString()),
            new Claim("email",    user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // Generates a cryptographically random opaque token (base64url, 32 bytes = 256 bits).
    // The raw value is returned to the caller once and never stored.
    public string GenerateRawRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncoder.Encode(bytes);
    }

    // SHA-256 hex of the raw token — safe to store in the database.
    public static string HashToken(string rawToken)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
