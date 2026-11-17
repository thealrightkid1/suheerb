using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.SystemTables;

/// <summary>Who changed what and when. Written automatically on every save.</summary>
public class AuditLog : Entity
{
    public DateTime At { get; set; }
    [StringLength(150)] public string? UserName { get; set; }
    public int? BranchId { get; set; }
    [Required, StringLength(100)] public string EntityName { get; set; } = "";
    [Required, StringLength(50)] public string EntityId { get; set; } = "";
    [Required, StringLength(20)] public string Action { get; set; } = "";
    /// <summary>JSON: { "Field": ["old", "new"] }</summary>
    public string? Changes { get; set; }
}

/// <summary>Gap-free, never-reused numbering per branch (students, vouchers, invoices, receipts).</summary>
public class NumberSequence : Entity
{
    public int BranchId { get; set; }
    [Required, StringLength(40)] public string Key { get; set; } = "";
    public int NextValue { get; set; } = 1;
}

public class ChangelogEntry : AuditableEntity
{
    [Required, StringLength(30)] public string Version { get; set; } = "";
    public DateOnly ReleasedOn { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = "";
    /// <summary>Markdown-lite: lines starting with "- " become bullets.</summary>
    [Required] public string Body { get; set; } = "";
}
