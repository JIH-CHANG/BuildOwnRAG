using ManufacturingAI.API.Extensions;
using ManufacturingAI.Core.Common;
using ManufacturingAI.Core.Models;
using ManufacturingAI.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BcryptNet = BCrypt.Net.BCrypt;

namespace ManufacturingAI.API.Controllers;

public record CreateUserRequest(string Email, string Password, UserRole Role);
public record InviteUserRequest(string Email, UserRole Role);
public record ChangeRoleRequest(UserRole Role);
public record UserResponse(Guid Id, string Email, string Role, string Status, DateTime CreatedAt);

[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "CanManageUsers")]
public class UsersController(IRepository<AppUser> userRepository) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> GetAll(CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var users = await userRepository.GetAllAsync(tenantId, ct);
        var items = users.Select(ToResponse).ToList();
        return Ok(this.ApiOk(new { items, total = items.Count }));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Create(
        [FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var tenantId = User.GetTenantId();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = request.Email,
            PasswordHash = BcryptNet.HashPassword(request.Password),
            Role = request.Role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await userRepository.AddAsync(user, ct);
        return result.Success
            ? Ok(this.ApiOk(ToResponse(result.Value!)))
            : BadRequest(this.ApiFail(result.Error!));
    }

    [HttpPost("invite")]
    public ActionResult<ApiResponse> Invite([FromBody] InviteUserRequest request)
    {
        // TODO: send invite email via email service
        return Ok(new ApiResponse(true, null, this.GetTraceId()));
    }

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult<ApiResponse>> ChangeRole(
        Guid id, [FromBody] ChangeRoleRequest request, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null || user.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("User not found."));

        user.Role = request.Role;
        var result = await userRepository.UpdateAsync(user, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse>> Deactivate(Guid id, CancellationToken ct)
    {
        var user = await userRepository.GetByIdAsync(id, ct);
        if (user is null || user.TenantId != User.GetTenantId())
            return NotFound(this.ApiFail("User not found."));

        user.IsActive = false;
        var result = await userRepository.UpdateAsync(user, ct);
        return result.Success
            ? Ok(new ApiResponse(true, null, this.GetTraceId()))
            : BadRequest(this.ApiFail(result.Error!));
    }

    private static UserResponse ToResponse(AppUser u) => new(
        u.Id,
        u.Email,
        u.Role is UserRole.TenantAdmin ? "TenantAdmin" : "Employee",
        u.IsActive ? "Active" : "Inactive",
        u.CreatedAt);
}
