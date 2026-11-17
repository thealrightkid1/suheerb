namespace BushiBan.Web.Domain;

/// <summary>Every table has an integer key.</summary>
public abstract class Entity
{
    public int Id { get; set; }
}

/// <summary>
/// Records who created/changed a row and supports soft delete.
/// Soft-deleted rows are hidden from every query by a global filter and can be restored.
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>Data that belongs to one branch. Queries only ever see the current branch's rows.</summary>
public interface IBranchScoped
{
    int BranchId { get; set; }
}

public abstract class BranchEntity : AuditableEntity, IBranchScoped
{
    public int BranchId { get; set; }
    public Organization.Branch? Branch { get; set; }
}
