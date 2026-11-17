using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace BushiBan.Web.Security;

/// <summary>Use on controllers/actions: [HasPermission(Permissions.Students.View)]. Several attributes = all required.</summary>
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public const string Prefix = "perm:";
    public HasPermissionAttribute(string permission) : base(Prefix + permission) { }
}

/// <summary>Passes when the user holds the permission or is a system admin. Pass several with "|" for "any of".</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permissions) => Permissions = permissions.Split('|');
    public string[] Permissions { get; }
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasPermission(requirement.Permissions)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasPermissionAttribute.Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[HasPermissionAttribute.Prefix.Length..]))
                .Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

public static class ClaimsPrincipalPermissionExtensions
{
    public static bool IsSystemAdmin(this System.Security.Claims.ClaimsPrincipal user) =>
        user.HasClaim(Permissions.ClaimType, Permissions.SystemAdmin);

    /// <summary>True when the user has any of the given permissions (or is a system admin).</summary>
    public static bool HasPermission(this System.Security.Claims.ClaimsPrincipal user, params string[] permissions) =>
        user.Identity?.IsAuthenticated == true &&
        (user.IsSystemAdmin() || permissions.Any(p => user.HasClaim(Permissions.ClaimType, p)));
}
