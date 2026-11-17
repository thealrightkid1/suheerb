using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Rendering;
using BushiBan.Web.Services;

namespace BushiBan.Web.Infrastructure;

public static class EnumHelpers
{
    public static string DisplayName(this Enum value)
    {
        var member = value.GetType().GetMember(value.ToString()).FirstOrDefault();
        return member?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? Humanize(value.ToString());
    }

    public static IEnumerable<SelectListItem> SelectList<TEnum>(TEnum? selected = null) where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => new SelectListItem(v.DisplayName(), Convert.ToInt32(v).ToString(), selected is not null && v.Equals(selected.Value)));

    public static string Humanize(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", " $1").Replace(" Id", "");
}

public static class Format
{
    private static readonly System.Globalization.CultureInfo Us = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    public static string Money(decimal? v) => v is null ? "" : v.Value.ToString("C2", Us);
    public static string Date(DateOnly? d) => d?.ToString("MMM d, yyyy", Us) ?? "";
    public static string Date(DateTime? d) => d?.ToString("MMM d, yyyy", Us) ?? "";
    public static string DateTime(DateTime? d) => d?.ToString("MMM d, yyyy h:mm tt", Us) ?? "";
    public static string Time(TimeOnly? t) => t?.ToString("h:mm tt", Us) ?? "";
}

/// <summary>Flash messages shown once at the top of the next page.</summary>
public static class FlashExtensions
{
    public static void Success(this Controller c, string message) => c.TempData["flash.success"] = message;
    public static void Error(this Controller c, string message) => c.TempData["flash.error"] = message;
}

/// <summary>Signed-in users with no branch (and not system admins) see a clear message instead of empty screens.</summary>
public class BranchRequiredFilter : IAsyncActionFilter
{
    private readonly ICurrentBranch _branch;
    public BranchRequiredFilter(ICurrentBranch branch) => _branch = branch;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var anonymous = context.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();
        var controller = context.RouteData.Values["controller"]?.ToString();
        if (!anonymous && controller is not ("Account" or "Profile") && context.HttpContext.User.Identity?.IsAuthenticated == true && _branch.BranchId is null)
        {
            context.Result = new ViewResult { ViewName = "~/Views/Shared/NoBranch.cshtml" };
            return;
        }
        await next();
    }
}

public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
        await next();
    });
}

/// <summary>Simple paging for lists.</summary>
public class PagedList<T>
{
    public PagedList(IReadOnlyList<T> items, int total, int page, int pageSize) { Items = items; Total = total; Page = page; PageSize = pageSize; }
    public IReadOnlyList<T> Items { get; }
    public int Total { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public static class PagingExtensions
{
    public static async Task<PagedList<T>> ToPagedAsync<T>(this IQueryable<T> query, int page, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var total = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(query, ct);
        var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(query.Skip((page - 1) * pageSize).Take(pageSize), ct);
        return new PagedList<T>(items, total, page, pageSize);
    }
}
