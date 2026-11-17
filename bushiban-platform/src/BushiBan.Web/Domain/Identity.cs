using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace BushiBan.Web.Domain.Identity;

public class AppUser : IdentityUser
{
    [StringLength(120)] public string? DisplayName { get; set; }
    /// <summary>The branch the user lands in after signing in.</summary>
    public int? HomeBranchId { get; set; }
    public Organization.Branch? HomeBranch { get; set; }
    /// <summary>Name of the permission preset last applied (informational; permissions are stored as claims).</summary>
    [StringLength(50)] public string? Preset { get; set; }
    public bool IsActive { get; set; } = true;
    [StringLength(300)] public string? ProfileImagePath { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Other branches this user may switch to. System admins can switch to any branch.</summary>
    public List<UserBranch> Branches { get; set; } = new();

    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? (UserName ?? Email ?? "") : DisplayName!;
}

public class UserBranch
{
    public string UserId { get; set; } = "";
    public AppUser? User { get; set; }
    public int BranchId { get; set; }
    public Organization.Branch? Branch { get; set; }
}
