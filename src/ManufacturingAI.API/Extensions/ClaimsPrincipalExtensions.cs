using ManufacturingAI.Core.Models;
using System.Security.Claims;

namespace ManufacturingAI.API.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetTenantId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue("tenantId")
            ?? throw new UnauthorizedAccessException("tenantId claim missing."));

    public static Guid GetUserId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue("userId")
            ?? throw new UnauthorizedAccessException("userId claim missing."));

    public static UserRole GetRole(this ClaimsPrincipal user)
        => Enum.Parse<UserRole>(user.FindFirstValue("role")
            ?? throw new UnauthorizedAccessException("role claim missing."));
}
