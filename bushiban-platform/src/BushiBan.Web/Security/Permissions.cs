using System.Reflection;

namespace BushiBan.Web.Security;

/// <summary>
/// Every permission in the app. Same names as the original CRM so presets carry over,
/// plus a few new ones for features the original didn't have (billing, communications…).
/// Stored on users as claims of type <see cref="ClaimType"/>.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";
    public const string SystemAdmin = "Permissions.SystemAdmin.All";

    public static class Students { public const string View = "Permissions.Students.View", Create = "Permissions.Students.Create", Edit = "Permissions.Students.Edit", Delete = "Permissions.Students.Delete"; }
    public static class Leads { public const string View = "Permissions.Leads.View", Create = "Permissions.Leads.Create", Edit = "Permissions.Leads.Edit", Delete = "Permissions.Leads.Delete"; }
    public static class Registrations { public const string View = "Permissions.Registrations.View", Manage = "Permissions.Registrations.Manage"; }
    public static class Attendance { public const string View = "Permissions.Attendance.View", Create = "Permissions.Attendance.Create", Edit = "Permissions.Attendance.Edit", Delete = "Permissions.Attendance.Delete"; }
    public static class Courses { public const string View = "Permissions.Courses.View", Create = "Permissions.Courses.Create", Edit = "Permissions.Courses.Edit", Delete = "Permissions.Courses.Delete"; }
    public static class RankingSystems { public const string View = "Permissions.RankingSystems.View", Create = "Permissions.RankingSystems.Create", Edit = "Permissions.RankingSystems.Edit", Delete = "Permissions.RankingSystems.Delete"; }
    public static class Ranks { public const string View = "Permissions.Ranks.View", Create = "Permissions.Ranks.Create", Edit = "Permissions.Ranks.Edit", Delete = "Permissions.Ranks.Delete", Promote = "Permissions.Ranks.Promote"; }
    public static class BeltSizes { public const string View = "Permissions.BeltSizes.View", Create = "Permissions.BeltSizes.Create", Edit = "Permissions.BeltSizes.Edit", Delete = "Permissions.BeltSizes.Delete"; }
    public static class Finance { public const string Reports = "Permissions.Finance.Reports"; }
    public static class Vouchers { public const string View = "Permissions.Vouchers.View", Create = "Permissions.Vouchers.Create", Edit = "Permissions.Vouchers.Edit", Delete = "Permissions.Vouchers.Delete"; }
    public static class MainAccounts { public const string View = "Permissions.MainAccounts.View", Manage = "Permissions.MainAccounts.Manage"; }
    public static class SubAccounts { public const string View = "Permissions.SubAccounts.View", Manage = "Permissions.SubAccounts.Manage"; }
    public static class DetailAccounts { public const string View = "Permissions.DetailAccounts.View", Manage = "Permissions.DetailAccounts.Manage"; }
    public static class FiscalYears { public const string View = "Permissions.FiscalYears.View", Manage = "Permissions.FiscalYears.Manage"; }
    public static class FeeStructures { public const string View = "Permissions.FeeStructures.View", Manage = "Permissions.FeeStructures.Manage"; }
    public static class Billing { public const string View = "Permissions.Billing.View", Manage = "Permissions.Billing.Manage", ReceivePayments = "Permissions.Billing.ReceivePayments"; }
    public static class Communications { public const string View = "Permissions.Communications.View", Send = "Permissions.Communications.Send", ManageTemplates = "Permissions.Communications.ManageTemplates"; }
    public static class Calendar { public const string View = "Permissions.Calendar.View", Manage = "Permissions.Calendar.Manage"; }
    public static class Users { public const string View = "Permissions.Users.View", Manage = "Permissions.Users.Manage"; }
    public static class Branches { public const string View = "Permissions.Branches.View", Manage = "Permissions.Branches.Manage"; }
    public static class Schools { public const string View = "Permissions.Schools.View", Manage = "Permissions.Schools.Manage"; }
    public static class Locations { public const string View = "Permissions.Locations.View", Manage = "Permissions.Locations.Manage"; }
    public static class FormBuilder { public const string Manage = "Permissions.FormBuilder.Manage"; }
    public static class Changelogs { public const string Manage = "Permissions.Changelogs.Manage"; }
    public static class AuditLog { public const string View = "Permissions.AuditLog.View"; }
    public static class Kiosk { public const string Attendance = "Permissions.Kiosk.Attendance", Registration = "Permissions.Kiosk.Registration"; }

    public record Feature(string Value, string Name, string Description);
    public record Group(string Name, IReadOnlyList<Feature> Features);

    /// <summary>Grouped list with plain-English descriptions, used on the user permissions screen.</summary>
    public static readonly IReadOnlyList<Group> Groups = new List<Group>
    {
        new("Students & CRM", new Feature[] {
            new(Students.View, "View Students", "Access student list and details"),
            new(Students.Create, "Create Students", "Add new students and link registrations"),
            new(Students.Edit, "Edit Students", "Modify student profiles and details"),
            new(Students.Delete, "Delete Students", "Remove (soft-delete) student records"),
            new(Leads.View, "View Leads", "Access inquiries and prospect list"),
            new(Leads.Create, "Create Leads", "Add new prospective leads"),
            new(Leads.Edit, "Edit Leads", "Update lead pipeline details and convert leads"),
            new(Leads.Delete, "Delete Leads", "Remove prospect leads"),
            new(Registrations.View, "View Registrations", "Inspect submitted kiosk forms"),
            new(Registrations.Manage, "Manage Registrations", "Link, archive, or discard registrations"),
        }),
        new("Communications & Calendar", new Feature[] {
            new(Communications.View, "View Messages", "See email and SMS history"),
            new(Communications.Send, "Send Messages", "Send email and SMS to students and leads"),
            new(Communications.ManageTemplates, "Manage Templates", "Create and edit message templates"),
            new(Calendar.View, "View Calendar", "See events and class schedule"),
            new(Calendar.Manage, "Manage Calendar", "Add, edit and remove events"),
        }),
        new("Attendance", new Feature[] {
            new(Attendance.View, "View Attendance", "Inspect check-in logs and history"),
            new(Attendance.Create, "Record Attendance", "Mark student check-ins"),
            new(Attendance.Edit, "Edit Attendance", "Modify check-in times and methods"),
            new(Attendance.Delete, "Delete Attendance", "Remove attendance records"),
        }),
        new("Academics & Belt Ranks", new Feature[] {
            new(Courses.View, "View Programs", "View martial arts programs and schedules"),
            new(Courses.Create, "Create Programs", "Add new programs and schedules"),
            new(Courses.Edit, "Edit Programs", "Update programs and schedule times"),
            new(Courses.Delete, "Delete Programs", "Remove martial arts programs"),
            new(RankingSystems.View, "View Ranking Systems", "Inspect ranking ladders and progression"),
            new(RankingSystems.Create, "Create Ranking Systems", "Define new ranking curriculums"),
            new(RankingSystems.Edit, "Edit Ranking Systems", "Update rank sequences and durations"),
            new(RankingSystems.Delete, "Delete Ranking Systems", "Remove ranking ladders"),
            new(Ranks.View, "View Ranks", "View list of belt ranks"),
            new(Ranks.Create, "Create Ranks", "Define new belt ranks"),
            new(Ranks.Edit, "Edit Ranks", "Modify belt names and colors"),
            new(Ranks.Delete, "Delete Ranks", "Remove belt ranks"),
            new(Ranks.Promote, "Promote Students", "Record belt promotions"),
            new(BeltSizes.View, "View Belt Sizes", "View belt sizing options"),
            new(BeltSizes.Create, "Create Belt Sizes", "Add new belt sizes"),
            new(BeltSizes.Edit, "Edit Belt Sizes", "Update belt measurements"),
            new(BeltSizes.Delete, "Delete Belt Sizes", "Remove belt sizes"),
        }),
        new("Billing", new Feature[] {
            new(Billing.View, "View Billing", "See contracts, invoices, payments and balances"),
            new(Billing.Manage, "Manage Billing", "Create contracts, invoices, run monthly billing, void"),
            new(Billing.ReceivePayments, "Receive Payments", "Record payments from students"),
            new(FeeStructures.View, "View Fee Structures", "Inspect tuition and fee tiers"),
            new(FeeStructures.Manage, "Manage Fee Structures", "Add and modify tuition and fees"),
        }),
        new("Accounting & Finance", new Feature[] {
            new(Finance.Reports, "Financial Reports", "Trial Balance, Ledger, Balance Sheet, Income Statement"),
            new(Vouchers.View, "View Vouchers", "View financial vouchers and transaction history"),
            new(Vouchers.Create, "Create Vouchers", "Record receipts, payments, and journal entries"),
            new(Vouchers.Edit, "Edit Vouchers", "Modify draft vouchers"),
            new(Vouchers.Delete, "Reverse Vouchers", "Reverse posted financial vouchers"),
            new(MainAccounts.View, "View Main Accounts", "View primary chart of accounts"),
            new(MainAccounts.Manage, "Manage Main Accounts", "Add, edit, or remove main accounts"),
            new(SubAccounts.View, "View Sub Accounts", "View branch sub-accounts"),
            new(SubAccounts.Manage, "Manage Sub Accounts", "Add, edit, or remove branch sub-accounts"),
            new(DetailAccounts.View, "View Detail Accounts", "View general ledger detail accounts"),
            new(DetailAccounts.Manage, "Manage Detail Accounts", "Add, edit, or remove detail accounts"),
            new(FiscalYears.View, "View Fiscal Years", "View accounting fiscal periods"),
            new(FiscalYears.Manage, "Manage Fiscal Years", "Configure and close fiscal years"),
        }),
        new("Administration & Settings", new Feature[] {
            new(Users.View, "View Users", "View staff and user accounts"),
            new(Users.Manage, "Manage Users & Permissions", "Create users and customize feature permissions"),
            new(Branches.View, "View Branches", "View school locations and branches"),
            new(Branches.Manage, "Manage Branches", "Add, edit, or configure branches"),
            new(Schools.View, "View Schools", "View schools that group branches"),
            new(Schools.Manage, "Manage Schools", "Add, edit, or remove schools"),
            new(Locations.View, "View Geographic Locations", "View cities, states, and countries"),
            new(Locations.Manage, "Manage Geographic Locations", "Add, edit, or remove cities, states, and countries"),
            new(FormBuilder.Manage, "Manage Form Builder", "Design and customize registration forms"),
            new(Changelogs.Manage, "Manage Changelogs", "Publish release notes"),
            new(AuditLog.View, "View Audit Log", "See who changed what"),
        }),
        new("Kiosks & Terminals", new Feature[] {
            new(Kiosk.Attendance, "Attendance Kiosk", "Operate the attendance check-in kiosk"),
            new(Kiosk.Registration, "Registration Kiosk", "Operate the self-service registration kiosk"),
        }),
    };

    public static IEnumerable<string> All => Groups.SelectMany(g => g.Features).Select(f => f.Value);

    /// <summary>Same presets as the original CRM, extended with the new permissions.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>
    {
        ["SystemAdmin"] = All.Append(SystemAdmin).ToArray(),
        ["Manager"] = All.Where(p => !p.Contains(".Schools.") && !p.Contains(".Branches.Manage") && !p.Contains(".Locations.Manage")).ToArray(),
        ["Accountant"] = new[] {
            Finance.Reports, MainAccounts.View, MainAccounts.Manage, SubAccounts.View, SubAccounts.Manage,
            DetailAccounts.View, DetailAccounts.Manage, Vouchers.View, Vouchers.Create, Vouchers.Edit, Vouchers.Delete,
            FiscalYears.View, FiscalYears.Manage, FeeStructures.View, FeeStructures.Manage,
            Billing.View, Billing.Manage, Billing.ReceivePayments, Students.View },
        ["Receptionist"] = new[] {
            Students.View, Students.Create, Students.Edit, Leads.View, Leads.Create, Leads.Edit,
            Registrations.View, Registrations.Manage, Attendance.View, Attendance.Create,
            Communications.View, Communications.Send, Calendar.View, Billing.View, Billing.ReceivePayments },
        ["AttendanceUser"] = new[] { Attendance.Create, Kiosk.Attendance },
        ["RegistrationUser"] = new[] { Registrations.Manage, Kiosk.Registration },
    };

    public static readonly IReadOnlyDictionary<string, string> PresetLabels = new Dictionary<string, string>
    {
        ["SystemAdmin"] = "System admin (all branches)",
        ["Manager"] = "Manager",
        ["Accountant"] = "Accountant",
        ["Receptionist"] = "Receptionist",
        ["AttendanceUser"] = "Attendance kiosk terminal",
        ["RegistrationUser"] = "Registration kiosk tablet",
    };
}
