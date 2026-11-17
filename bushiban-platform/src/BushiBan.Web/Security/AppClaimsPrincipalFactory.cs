using System.Security.Claims;
using BushiBan.Web.Data;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BushiBan.Web.Security;

/// <summary>Adds display name, home branch and allowed branches to the sign-in cookie. Permission claims come from the user's claims table.</summary>
public class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<AppUser>
{
    private readonly AppDbContext _db;

    public AppClaimsPrincipalFactory(UserManager<AppUser> users, IOptions<IdentityOptions> options, AppDbContext db) : base(users, options) => _db = db;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim("display_name", user.Name));
        if (user.HomeBranchId is int home) id.AddClaim(new Claim(CurrentBranch.HomeBranchClaim, home.ToString()));
        var branches = await _db.UserBranches.Where(b => b.UserId == user.Id).Select(b => b.BranchId).ToListAsync();
        if (branches.Count > 0) id.AddClaim(new Claim(CurrentBranch.BranchesClaim, string.Join(",", branches)));
        return id;
    }
}
