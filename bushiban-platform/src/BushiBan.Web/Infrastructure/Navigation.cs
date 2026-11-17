using BushiBan.Web.Security;
using P = BushiBan.Web.Security.Permissions;

namespace BushiBan.Web.Infrastructure;

/// <summary>
/// The sidebar. One place lists every screen, its icon (Bootstrap Icons) and the permission needed to see it.
/// Items the user can't use are hidden; empty sections disappear.
/// </summary>
public static class Navigation
{
    public record Item(string Label, string Icon, string Controller, string Action, string[] AnyOf, string? Badge = null);
    public record Section(string? Title, Item[] Items);

    public static readonly Section[] Sections =
    {
        new(null, new[] { new Item("Dashboard", "speedometer2", "Dashboard", "Index", Array.Empty<string>()) }),
        new("CRM", new[]
        {
            new Item("Leads", "person-plus", "Leads", "Index", new[] { P.Leads.View }),
            new Item("Pipeline board", "kanban", "Leads", "Board", new[] { P.Leads.View }),
            new Item("Follow-ups", "telephone-forward", "Leads", "FollowUps", new[] { P.Leads.View }, "followups"),
            new Item("Registrations", "file-earmark-text", "Registrations", "Index", new[] { P.Registrations.View, P.Registrations.Manage }, "registrations"),
            new Item("Students", "people", "Students", "Index", new[] { P.Students.View }),
            new Item("Messages", "chat-left-text", "Messages", "Index", new[] { P.Communications.View, P.Communications.Send }),
            new Item("Calendar", "calendar3", "Calendar", "Index", new[] { P.Calendar.View }),
        }),
        new("Attendance", new[]
        {
            new Item("Take attendance", "check2-square", "Attendance", "Roll", new[] { P.Attendance.Create }),
            new Item("Attendance records", "clipboard-data", "Attendance", "Index", new[] { P.Attendance.View }),
            new Item("Check-in kiosk", "tablet", "Kiosk", "Attendance", new[] { P.Kiosk.Attendance }),
            new Item("Registration kiosk", "tablet-landscape", "Kiosk", "Register", new[] { P.Kiosk.Registration }),
        }),
        new("Academics", new[]
        {
            new Item("Programs", "mortarboard", "Courses", "Index", new[] { P.Courses.View }),
            new Item("Belt promotions", "award", "Promotions", "Index", new[] { P.Ranks.Promote, P.Ranks.View }),
            new Item("Ranking systems", "bar-chart-steps", "RankingSystems", "Index", new[] { P.RankingSystems.View }),
            new Item("Ranks", "trophy", "Ranks", "Index", new[] { P.Ranks.View }),
            new Item("Belt sizes", "rulers", "BeltSizes", "Index", new[] { P.BeltSizes.View }),
        }),
        new("Billing", new[]
        {
            new Item("Billing overview", "cash-coin", "Billing", "Index", new[] { P.Billing.View }),
            new Item("Contracts", "file-earmark-check", "Contracts", "Index", new[] { P.Billing.View }),
            new Item("Invoices", "receipt", "Invoices", "Index", new[] { P.Billing.View }),
            new Item("Payments", "credit-card", "Payments", "Index", new[] { P.Billing.View, P.Billing.ReceivePayments }),
            new Item("Fee structures", "tags", "FeeStructures", "Index", new[] { P.FeeStructures.View }),
        }),
        new("Accounting", new[]
        {
            new Item("Vouchers", "journal-text", "Vouchers", "Index", new[] { P.Vouchers.View }),
            new Item("Chart of accounts", "diagram-3", "ChartOfAccounts", "Index", new[] { P.MainAccounts.View, P.SubAccounts.View, P.DetailAccounts.View }),
            new Item("Main accounts", "folder", "MainAccounts", "Index", new[] { P.MainAccounts.View }),
            new Item("Sub accounts", "folder2-open", "SubAccounts", "Index", new[] { P.SubAccounts.View }),
            new Item("Detail accounts", "files", "DetailAccounts", "Index", new[] { P.DetailAccounts.View }),
            new Item("Fiscal years", "calendar-range", "FiscalYears", "Index", new[] { P.FiscalYears.View }),
            new Item("Trial balance", "scale", "Reports", "TrialBalance", new[] { P.Finance.Reports }),
            new Item("Account ledger", "book", "Reports", "Ledger", new[] { P.Finance.Reports }),
            new Item("Income statement", "graph-up-arrow", "Reports", "IncomeStatement", new[] { P.Finance.Reports }),
            new Item("Balance sheet", "clipboard2-pulse", "Reports", "BalanceSheet", new[] { P.Finance.Reports }),
        }),
        new("Setup", new[]
        {
            new Item("Form builder", "ui-checks", "FormBuilder", "Index", new[] { P.FormBuilder.Manage }),
            new Item("Message templates", "envelope-paper", "MessageTemplates", "Index", new[] { P.Communications.ManageTemplates }),
            new Item("Branch settings", "sliders", "Settings", "Index", new[] { P.Branches.Manage, P.Billing.Manage }),
            new Item("Cities", "geo-alt", "Cities", "Index", new[] { P.Locations.View }),
            new Item("States", "map", "States", "Index", new[] { P.Locations.View }),
            new Item("Countries", "globe-americas", "Countries", "Index", new[] { P.Locations.View }),
        }),
        new("Administration", new[]
        {
            new Item("Users", "person-gear", "Users", "Index", new[] { P.Users.View }),
            new Item("Branches", "shop", "Branches", "Index", new[] { P.Branches.View }),
            new Item("Schools", "building", "Schools", "Index", new[] { P.Schools.View }),
            new Item("Audit log", "shield-check", "AuditLog", "Index", new[] { P.AuditLog.View }),
            new Item("What's new", "stars", "Changelog", "Index", Array.Empty<string>()),
        }),
    };

    public static IEnumerable<Section> For(System.Security.Claims.ClaimsPrincipal user) =>
        Sections.Select(s => s with { Items = s.Items.Where(i => i.AnyOf.Length == 0 || user.HasPermission(i.AnyOf)).ToArray() })
                .Where(s => s.Items.Length > 0);
}
