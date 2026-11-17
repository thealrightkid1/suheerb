using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Forms;

/// <summary>A kiosk form (admission, enrollment agreement, change form…). Content lives in versions.</summary>
public class FormDefinition : BranchEntity
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    public FormCategory Category { get; set; } = FormCategory.Admission;
    /// <summary>Shown on the registration kiosk when true and a version is published.</summary>
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    /// <summary>For agreements: program this form enrolls into (used when creating the student/contract).</summary>
    public int? CourseId { get; set; }
    public Academics.Course? Course { get; set; }
    public int? PublishedVersionId { get; set; }
    public int? DraftVersionId { get; set; }
    public List<FormVersion> Versions { get; set; } = new();
}

/// <summary>
/// Immutable once published. Submissions point at the exact version signed,
/// so old paperwork never changes when the form is edited.
/// </summary>
public class FormVersion : AuditableEntity
{
    public int FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }
    public int VersionNumber { get; set; }
    /// <summary>JSON array of fields. See Services/Forms/FormSchema.cs.</summary>
    [Required] public string SchemaJson { get; set; } = "[]";
    public DateTime? PublishedAt { get; set; }
}

public class FormSubmission : BranchEntity
{
    public int FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }
    public int FormVersionId { get; set; }
    public FormVersion? FormVersion { get; set; }
    public DateTime SubmittedAt { get; set; }
    /// <summary>JSON object: field key → value (string or string[]).</summary>
    [Required] public string DataJson { get; set; } = "{}";
    // Denormalised for fast listing and search.
    [StringLength(80)] public string? FirstName { get; set; }
    [StringLength(80)] public string? LastName { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(150)] public string? Email { get; set; }
    public SubmissionStatus Status { get; set; } = SubmissionStatus.New;
    public int? LeadId { get; set; }
    public People.Lead? Lead { get; set; }
    public int? StudentId { get; set; }
    public People.Student? Student { get; set; }
    [StringLength(60)] public string? IpAddress { get; set; }
    [StringLength(150)] public string? KioskUser { get; set; }
    public List<SubmissionSignature> Signatures { get; set; } = new();

    public string FullName => $"{FirstName} {LastName}".Trim();
}

public class SubmissionSignature : Entity
{
    public int FormSubmissionId { get; set; }
    public FormSubmission? FormSubmission { get; set; }
    [Required, StringLength(80)] public string FieldKey { get; set; } = "";
    /// <summary>PNG image of the signature.</summary>
    public byte[] Png { get; set; } = Array.Empty<byte>();
    public DateTime SignedAt { get; set; }
}
