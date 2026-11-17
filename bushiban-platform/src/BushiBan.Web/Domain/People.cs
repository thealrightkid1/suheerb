using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Domain.Academics;
using BushiBan.Web.Domain.Organization;

namespace BushiBan.Web.Domain.People;

/// <summary>Fields shared by leads and students (the original CRM duplicated these on both screens).</summary>
public abstract class PersonBase : BranchEntity
{
    public bool IsActive { get; set; } = true;
    [StringLength(20)] public string? Title { get; set; }
    [Required, StringLength(80)] public string FirstName { get; set; } = "";
    [Required, StringLength(80)] public string LastName { get; set; } = "";
    [StringLength(120)] public string? FatherName { get; set; }
    [StringLength(120)] public string? MotherName { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public AgeGroup? AgeGroup { get; set; }
    public Gender? Gender { get; set; }
    public int? BeltSizeId { get; set; }
    public BeltSize? BeltSize { get; set; }

    [StringLength(30)] public string? Phone1Label { get; set; } = "Mobile";
    [StringLength(30)] public string? Phone1 { get; set; }
    [StringLength(30)] public string? Phone2Label { get; set; }
    [StringLength(30)] public string? Phone2 { get; set; }
    [StringLength(30)] public string? Email1Label { get; set; } = "Personal";
    [EmailAddress, StringLength(150)] public string? Email1 { get; set; }
    [StringLength(30)] public string? Email2Label { get; set; }
    [EmailAddress, StringLength(150)] public string? Email2 { get; set; }
    [StringLength(200)] public string? Address1 { get; set; }
    [StringLength(200)] public string? Address2 { get; set; }
    public int? CityId { get; set; }
    public City? City { get; set; }
    [StringLength(20)] public string? ZipCode { get; set; }

    public int? CourseId { get; set; }
    public Course? Course { get; set; }
    public int? RankId { get; set; }
    public Rank? Rank { get; set; }
    public ProspectPhase ProspectPhase { get; set; } = ProspectPhase.NoAppointment;

    [StringLength(2000)] public string? MedicalConcerns { get; set; }
    [StringLength(4000)] public string? AdditionalInformation { get; set; }
    [StringLength(120)] public string? MarketingSource { get; set; }
    [StringLength(300)] public string? MarketingSourceDetail { get; set; }
    [StringLength(120)] public string? Meeting { get; set; }
    [StringLength(300)] public string? MeetingDetail { get; set; }

    [StringLength(200)] public string? Facebook { get; set; }
    [StringLength(200)] public string? Instagram { get; set; }
    [StringLength(200)] public string? LinkedIn { get; set; }
    [StringLength(200)] public string? Pinterest { get; set; }
    [StringLength(200)] public string? Twitter { get; set; }
    [StringLength(200)] public string? Website { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();

    public int? AgeOn(DateOnly today)
    {
        if (DateOfBirth is not { } dob) return null;
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return age;
    }
}

public class Lead : PersonBase
{
    public DateOnly InquiryDate { get; set; }
    public LeadSource Source { get; set; } = LeadSource.WalkIn;
    /// <summary>New: when staff should next contact this lead. Drives the follow-up list.</summary>
    public DateOnly? NextFollowUpDate { get; set; }
    public string? AssignedToUserId { get; set; }
    public Identity.AppUser? AssignedTo { get; set; }
    /// <summary>Set when the lead was converted to a student.</summary>
    public int? ConvertedStudentId { get; set; }
    public Student? ConvertedStudent { get; set; }
    public DateTime? ConvertedAt { get; set; }
}

public class Student : PersonBase
{
    /// <summary>Branch-unique, never reused (e.g. PAS-00042).</summary>
    [Required, StringLength(30)] public string StudentNo { get; set; } = "";
    public DateOnly EnrollmentDate { get; set; }
    /// <summary>Monthly discount applied on top of the program's fee structure.</summary>
    [Range(0, 1_000_000)] public decimal Discount { get; set; }
    /// <summary>When the student reached their current rank. Drives promotion readiness.</summary>
    public DateOnly? RankSince { get; set; }
    /// <summary>New: 4–6 digit PIN for the attendance kiosk. Unique within a branch.</summary>
    [StringLength(6)] public string? KioskPin { get; set; }

    public bool IsEmail1Subscriber { get; set; } = true;
    public bool IsEmail2Subscriber { get; set; }
    public bool IsPhone1Subscriber { get; set; } = true;
    public bool IsPhone2Subscriber { get; set; }
    public bool IsPostCardSubscriber { get; set; }

    public int? LeadId { get; set; }
    [StringLength(300)] public string? PhotoPath { get; set; }

    public List<RankPromotion> Promotions { get; set; } = new();
}

/// <summary>Timeline entry on a lead or student: notes, calls, follow-up tasks, stage changes.</summary>
public class Activity : BranchEntity
{
    public int? LeadId { get; set; }
    public Lead? Lead { get; set; }
    public int? StudentId { get; set; }
    public Student? Student { get; set; }
    public ActivityType Type { get; set; } = ActivityType.Note;
    [Required, StringLength(4000)] public string Body { get; set; } = "";
    /// <summary>For follow-up tasks: when it's due.</summary>
    public DateTime? DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? UserId { get; set; }
}

/// <summary>History of belt promotions.</summary>
public class RankPromotion : BranchEntity
{
    public int StudentId { get; set; }
    public Student? Student { get; set; }
    public int? FromRankId { get; set; }
    public Rank? FromRank { get; set; }
    public int ToRankId { get; set; }
    public Rank? ToRank { get; set; }
    public DateOnly PromotedOn { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
}
