using ManufacturingAI.API.Auth;
using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Mvc;
using BcryptNet = BCrypt.Net.BCrypt;

namespace ManufacturingAI.API.Controllers;

public record LoginRequest(string Email, string Password, string DeviceInfo = "Unknown");
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);
public record UserInfoDto(Guid Id, string Email, string Role, Guid TenantId, string Plan);
public record LoginResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserInfoDto User);

[ApiController]
[Route("api/v1/auth")]
public class AuthController(
    IUserRepository userRepository,
    IRefreshTokenRepository refreshTokenRepository,
    IRepository<Tenant> tenantRepository,
    ITokenService tokenService) : ControllerBase
{
    private const int MaxActiveRefreshTokens = 5;
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(7);

    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        var user = await userRepository.FindByEmailAsync(request.Email, ct);

        if (user is null || !user.IsActive || !BcryptNet.Verify(request.Password, user.PasswordHash))
            return Unauthorized(this.ApiFail("Invalid email or password."));

        await EnforceDeviceLimitAsync(user.Id, ct);

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user, request.DeviceInfo, ct);
        var userInfo = await BuildUserInfoAsync(user, ct);
        return Ok(this.ApiOk(new LoginResponse(accessToken, rawRefreshToken, DateTime.UtcNow.AddMinutes(15), userInfo)));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LoginResponse>>> Refresh(
        [FromBody] RefreshRequest request, CancellationToken ct)
    {
        var tokenHash = TokenService.HashToken(request.RefreshToken);
        var storedToken = await refreshTokenRepository.GetByTokenHashAsync(tokenHash, ct);

        if (storedToken is null || storedToken.IsRevoked || storedToken.ExpiresAt <= DateTime.UtcNow)
            return Unauthorized(this.ApiFail("Invalid or expired refresh token."));

        var user = await userRepository.GetByIdAsync(storedToken.UserId, ct);
        if (user is null || !user.IsActive)
            return Unauthorized(this.ApiFail("User not found or inactive."));

        // Token rotation: mark current token as revoked before issuing new one
        storedToken.IsRevoked = true;
        await refreshTokenRepository.UpdateAsync(storedToken, ct);

        await EnforceDeviceLimitAsync(user.Id, ct);

        var (accessToken, rawRefreshToken) = await IssueTokensAsync(user, storedToken.DeviceInfo, ct);
        var userInfo = await BuildUserInfoAsync(user, ct);
        return Ok(this.ApiOk(new LoginResponse(accessToken, rawRefreshToken, DateTime.UtcNow.AddMinutes(15), userInfo)));
    }

    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse>> Logout(
        [FromBody] LogoutRequest request, CancellationToken ct)
    {
        var tokenHash = TokenService.HashToken(request.RefreshToken);
        var storedToken = await refreshTokenRepository.GetByTokenHashAsync(tokenHash, ct);

        if (storedToken is not null && !storedToken.IsRevoked)
        {
            storedToken.IsRevoked = true;
            await refreshTokenRepository.UpdateAsync(storedToken, ct);
        }

        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    // ── Helpers ────────────────────────────────────────────────

    private async Task<UserInfoDto> BuildUserInfoAsync(AppUser user, CancellationToken ct)
    {
        var tenant = await tenantRepository.GetByIdAsync(user.TenantId, ct);
        var plan = tenant?.Plan is TenantPlan.Free ? "Free" : "Pro";
        var role = user.Role is UserRole.TenantAdmin ? "TenantAdmin" : "Employee";
        return new UserInfoDto(user.Id, user.Email, role, user.TenantId, plan);
    }

    private async Task<(string AccessToken, string RawRefreshToken)> IssueTokensAsync(
        AppUser user, string deviceInfo, CancellationToken ct)
    {
        var accessToken = tokenService.GenerateAccessToken(user);
        var rawRefreshToken = tokenService.GenerateRawRefreshToken();

        var entity = new AppRefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantId = user.TenantId,
            TokenHash = TokenService.HashToken(rawRefreshToken),
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenTtl),
            IsRevoked = false,
            DeviceInfo = deviceInfo,
            CreatedAt = DateTime.UtcNow
        };

        await refreshTokenRepository.AddAsync(entity, ct);
        return (accessToken, rawRefreshToken);
    }

    // Revokes the oldest active token when the user already has MaxActiveRefreshTokens.
    private async Task EnforceDeviceLimitAsync(Guid userId, CancellationToken ct)
    {
        var active = await refreshTokenRepository.GetActiveByUserAsync(userId, ct);
        if (active.Count < MaxActiveRefreshTokens) return;

        // GetActiveByUserAsync is ordered by CreatedAt asc — oldest is first
        var oldest = active[0];
        oldest.IsRevoked = true;
        await refreshTokenRepository.UpdateAsync(oldest, ct);
    }
}
