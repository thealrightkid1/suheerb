using System.Linq.Expressions;
using System.Text.Json;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.Accounting;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Domain.Billing;
using BushiBan.Web.Domain.Communication;
using BushiBan.Web.Domain.Forms;
using BushiBan.Web.Domain.Identity;
using BushiBan.Web.Domain.Organization;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Domain.SystemTables;
using BushiBan.Web.Services;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace BushiBan.Web.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    private readonly ICurrentBranch? _branch;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentBranch? branch = null) : base(options)
    {
        _branch = branch;
    }

    // Used by the global query filters. EF re-reads these for every query.
    private int? CurrentBranchId => _branch?.BranchId;
    private bool BranchFilterDisabled => _branch?.FilterDisabled ?? true;

    // Organization
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<State> States => Set<State>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<School> Schools => Set<School>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    // Academics
    public DbSet<Rank> Ranks => Set<Rank>();
    public DbSet<RankingSystem> RankingSystems => Set<RankingSystem>();
    public DbSet<RankingSystemRank> RankingSystemRanks => Set<RankingSystemRank>();
    public DbSet<BeltSize> BeltSizes => Set<BeltSize>();
    public DbSet<FeeStructure> FeeStructures => Set<FeeStructure>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseRankingSystem> CourseRankingSystems => Set<CourseRankingSystem>();
    public DbSet<ClassSchedule> ClassSchedules => Set<ClassSchedule>();
    // People
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<RankPromotion> RankPromotions => Set<RankPromotion>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    // Forms
    public DbSet<FormDefinition> FormDefinitions => Set<FormDefinition>();
    public DbSet<FormVersion> FormVersions => Set<FormVersion>();
    public DbSet<FormSubmission> FormSubmissions => Set<FormSubmission>();
    public DbSet<SubmissionSignature> SubmissionSignatures => Set<SubmissionSignature>();
    // Accounting
    public DbSet<MainAccount> MainAccounts => Set<MainAccount>();
    public DbSet<SubAccount> SubAccounts => Set<SubAccount>();
    public DbSet<DetailAccount> DetailAccounts => Set<DetailAccount>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<VoucherEntry> VoucherEntries => Set<VoucherEntry>();
    // Billing
    public DbSet<StudentContract> StudentContracts => Set<StudentContract>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<AccountingSettings> AccountingSettings => Set<AccountingSettings>();
    // Communication
    public DbSet<MessageLog> MessageLogs => Set<MessageLog>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    // System
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<ChangelogEntry> ChangelogEntries => Set<ChangelogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            // SQLite can't sum or sort decimals; store money as REAL there (local runs and tests only).
            builder.Properties<decimal>().HaveConversion<double>();
            builder.Properties<decimal?>().HaveConversion<double?>();
        }
        else
        {
            builder.Properties<decimal>().HavePrecision(18, 2);
            builder.Properties<decimal?>().HavePrecision(18, 2);
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // No cascading deletes by default: rows are soft-deleted, and SQL Server rejects multiple cascade paths.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        // Child rows that belong wholly to their parent.
        b.Entity<RankingSystemRank>().HasOne(x => x.RankingSystem).WithMany(x => x.Ranks).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CourseRankingSystem>().HasOne(x => x.Course).WithMany(x => x.RankingSystems).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ClassSchedule>().HasOne(x => x.Course).WithMany(x => x.Schedule).OnDelete(DeleteBehavior.Cascade);
        b.Entity<VoucherEntry>().HasOne(x => x.Voucher).WithMany(x => x.Entries).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InvoiceLine>().HasOne(x => x.Invoice).WithMany(x => x.Lines).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PaymentAllocation>().HasOne(x => x.Payment).WithMany(x => x.Allocations).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PaymentAllocation>().HasOne(x => x.Invoice).WithMany(x => x.Allocations).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SubmissionSignature>().HasOne(x => x.FormSubmission).WithMany(x => x.Signatures).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FormVersion>().HasOne(x => x.FormDefinition).WithMany(x => x.Versions).OnDelete(DeleteBehavior.Cascade);

        b.Entity<UserBranch>().HasKey(x => new { x.UserId, x.BranchId });
        b.Entity<UserBranch>().HasOne(x => x.User).WithMany(x => x.Branches).HasForeignKey(x => x.UserId);
        b.Entity<Lead>().HasOne(x => x.ConvertedStudent).WithMany().HasForeignKey(x => x.ConvertedStudentId);
        b.Entity<Voucher>().HasOne(x => x.ReversalOf).WithMany().HasForeignKey(x => x.ReversalOfId);
        b.Entity<Activity>().HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId);
        b.Entity<Activity>().HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId);

        // Uniqueness and lookups. "[IsDeleted] = 0" filters keep soft-deleted rows from blocking reuse of names.
        const string notDeleted = "[IsDeleted] = 0";
        b.Entity<Student>().HasIndex(x => new { x.BranchId, x.StudentNo }).IsUnique();
        b.Entity<Student>().HasIndex(x => new { x.BranchId, x.KioskPin }).IsUnique().HasFilter("[KioskPin] IS NOT NULL AND [IsDeleted] = 0");
        b.Entity<Student>().HasIndex(x => new { x.BranchId, x.LastName, x.FirstName });
        b.Entity<Lead>().HasIndex(x => new { x.BranchId, x.LastName, x.FirstName });
        b.Entity<Lead>().HasIndex(x => new { x.BranchId, x.ProspectPhase });
        b.Entity<Rank>().HasIndex(x => new { x.BranchId, x.Name }).IsUnique().HasFilter(notDeleted);
        b.Entity<BeltSize>().HasIndex(x => new { x.BranchId, x.Name }).IsUnique().HasFilter(notDeleted);
        b.Entity<Course>().HasIndex(x => new { x.BranchId, x.Name }).IsUnique().HasFilter(notDeleted);
        b.Entity<RankingSystem>().HasIndex(x => new { x.BranchId, x.Name }).IsUnique().HasFilter(notDeleted);
        b.Entity<FeeStructure>().HasIndex(x => new { x.BranchId, x.Name }).IsUnique().HasFilter(notDeleted);
        b.Entity<MainAccount>().HasIndex(x => new { x.BranchId, x.Code }).IsUnique().HasFilter(notDeleted);
        b.Entity<SubAccount>().HasIndex(x => new { x.BranchId, x.Code }).IsUnique().HasFilter(notDeleted);
        b.Entity<DetailAccount>().HasIndex(x => new { x.BranchId, x.Code }).IsUnique().HasFilter(notDeleted);
        b.Entity<Voucher>().HasIndex(x => new { x.BranchId, x.VoucherNo }).IsUnique();
        b.Entity<Voucher>().HasIndex(x => new { x.BranchId, x.Date });
        b.Entity<Invoice>().HasIndex(x => new { x.BranchId, x.InvoiceNo }).IsUnique();
        b.Entity<Invoice>().HasIndex(x => new { x.ContractId, x.PeriodMonth }).IsUnique().HasFilter("[PeriodMonth] IS NOT NULL AND [IsDeleted] = 0");
        b.Entity<Payment>().HasIndex(x => new { x.BranchId, x.ReceiptNo }).IsUnique();
        b.Entity<AttendanceRecord>().HasIndex(x => new { x.BranchId, x.Date });
        b.Entity<AttendanceRecord>().HasIndex(x => new { x.StudentId, x.Date });
        b.Entity<FormSubmission>().HasIndex(x => new { x.BranchId, x.Status, x.SubmittedAt });
        b.Entity<MessageLog>().HasIndex(x => new { x.BranchId, x.CreatedAt });
        b.Entity<NumberSequence>().HasIndex(x => new { x.BranchId, x.Key }).IsUnique();
        b.Entity<AuditLog>().HasIndex(x => new { x.EntityName, x.EntityId });
        b.Entity<AuditLog>().HasIndex(x => x.At);
        b.Entity<AccountingSettings>().HasIndex(x => x.BranchId).IsUnique();
        b.Entity<City>().HasIndex(x => new { x.StateId, x.Name });

        // Global filters: hide soft-deleted rows, and keep each branch's data to itself.
        foreach (var type in b.Model.GetEntityTypes().Select(t => t.ClrType))
        {
            if (typeof(IBranchScoped).IsAssignableFrom(type) && typeof(AuditableEntity).IsAssignableFrom(type))
                GetType().GetMethod(nameof(ApplyBranchFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(type).Invoke(this, new object[] { b });
            else if (typeof(AuditableEntity).IsAssignableFrom(type) && type.BaseType == typeof(AuditableEntity))
                GetType().GetMethod(nameof(ApplySoftDeleteFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .MakeGenericMethod(type).Invoke(this, new object[] { b });
        }
    }

    private void ApplyBranchFilter<T>(ModelBuilder b) where T : AuditableEntity, IBranchScoped
    {
        // PersonBase subclasses each get their own filter (base type isn't mapped).
        b.Entity<T>().HasQueryFilter(e => !e.IsDeleted && (BranchFilterDisabled || e.BranchId == CurrentBranchId));
    }

    private void ApplySoftDeleteFilter<T>(ModelBuilder b) where T : AuditableEntity
    {
        b.Entity<T>().HasQueryFilter(e => !e.IsDeleted);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(true, cancellationToken);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var audits = BeforeSave();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (AfterSave(audits)) base.SaveChanges(acceptAllChangesOnSuccess);
        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var audits = BeforeSave();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (AfterSave(audits)) await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        return result;
    }

    private static readonly HashSet<string> AuditSkip = new()
    {
        nameof(AuditableEntity.CreatedAt), nameof(AuditableEntity.CreatedBy), nameof(AuditableEntity.UpdatedAt),
        nameof(AuditableEntity.UpdatedBy), nameof(AuditableEntity.DeletedAt), nameof(AuditableEntity.DeletedBy),
        "ConcurrencyStamp", "SecurityStamp", "PasswordHash",
    };

    private sealed record PendingAudit(EntityEntry Entry, AuditLog Log);

    private List<PendingAudit> BeforeSave()
    {
        var now = DateTime.UtcNow;
        var user = _branch?.UserName ?? "system";
        var pending = new List<PendingAudit>();

        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditLog || entry.State is EntityState.Unchanged or EntityState.Detached) continue;

            if (entry.Entity is AuditableEntity a)
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        a.CreatedAt = now; a.CreatedBy ??= user;
                        if (a is IBranchScoped bs && bs.BranchId == 0 && CurrentBranchId is int id) bs.BranchId = id;
                        break;
                    case EntityState.Modified:
                        a.UpdatedAt = now; a.UpdatedBy = user;
                        break;
                    case EntityState.Deleted:
                        // Soft delete: keep the row, hide it everywhere.
                        entry.State = EntityState.Modified;
                        a.IsDeleted = true; a.DeletedAt = now; a.DeletedBy = user;
                        break;
                }
            }

            var name = entry.Metadata.ClrType.Name;
            if (name is nameof(SubmissionSignature) or nameof(NumberSequence) or "IdentityUserToken`1" or "IdentityUserLogin`1") continue;

            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => entry.Entity is AuditableEntity { IsDeleted: true } ae && entry.Property(nameof(AuditableEntity.IsDeleted)).IsModified ? "Deleted" : "Updated",
            };
            var changes = new Dictionary<string, object?[]>();
            foreach (var p in entry.Properties)
            {
                if (AuditSkip.Contains(p.Metadata.Name) || p.Metadata.IsPrimaryKey()) continue;
                if (entry.State == EntityState.Modified && !p.IsModified) continue;
                if (entry.State == EntityState.Modified && Equals(p.OriginalValue, p.CurrentValue)) continue;
                changes[p.Metadata.Name] = entry.State == EntityState.Added
                    ? new[] { Short(p.CurrentValue) }
                    : new[] { Short(p.OriginalValue), Short(p.CurrentValue) };
            }
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            pending.Add(new PendingAudit(entry, new AuditLog
            {
                At = now,
                UserName = user,
                BranchId = (entry.Entity as IBranchScoped)?.BranchId ?? CurrentBranchId,
                EntityName = name,
                EntityId = "",
                Action = action,
                Changes = JsonSerializer.Serialize(changes),
            }));
        }
        return pending;
    }

    private static object? Short(object? v) => v switch
    {
        null => null,
        byte[] => "(binary)",
        string s when s.Length > 200 => s[..200] + "…",
        _ => v.ToString(),
    };

    /// <summary>Writes audit rows once keys are known. Returns true when there is something to save.</summary>
    private bool AfterSave(List<PendingAudit> pending)
    {
        if (pending.Count == 0) return false;
        foreach (var p in pending)
        {
            var key = p.Entry.Metadata.FindPrimaryKey();
            p.Log.EntityId = key is null ? "" : string.Join(",", key.Properties.Select(k => p.Entry.Property(k.Name).CurrentValue));
            AuditLogs.Add(p.Log);
        }
        return true;
    }
}
