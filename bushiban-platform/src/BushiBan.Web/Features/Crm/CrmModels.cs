using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Crm;

// ---------- Shared ----------

/// <summary>The "add to timeline" form on lead and student pages.</summary>
public class ActivityInput
{
    public ActivityType Type { get; set; } = ActivityType.Note;
    [StringLength(4000)] public string? Body { get; set; }
    /// <summary>Local date/time in the branch's time zone (follow-up tasks only).</summary>
    public DateTime? DueAt { get; set; }

    public static readonly ActivityType[] Choices =
        { ActivityType.Note, ActivityType.Call, ActivityType.Email, ActivityType.SMS, ActivityType.Meeting, ActivityType.FollowUp };
}

/// <summary>Model for the timeline partial (Views/Crm/_Timeline.cshtml).</summary>
public record TimelineModel(string Controller, int OwnerId, List<TimelineItem> Items, bool CanAdd, Func<DateTime, DateTime> ToLocal, DateTime NowUtc);

// ---------- Leads ----------

public class LeadFilter
{
    public string? Q { get; set; }
    public ProspectPhase? Phase { get; set; }
    public int? CourseId { get; set; }
    public LeadSource? Source { get; set; }
    /// <summary>"yes", "no" or empty for both.</summary>
    public string? Active { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>"newest" (default), "oldest", "name" or "followup".</summary>
    public string? Sort { get; set; }

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Q) || Phase is not null || CourseId is not null || Source is not null
                              || !string.IsNullOrEmpty(Active) || From is not null || To is not null;
}

public record LeadRow(int Id, string Name, string? Phone, string? Email, string? City, DateOnly? DateOfBirth,
    string? Rank, string? RankColor, string? Program, DateOnly InquiryDate, ProspectPhase Phase, bool IsActive,
    int? ConvertedStudentId, DateOnly? NextFollowUpDate, LeadSource Source);

public class LeadIndexModel
{
    public LeadFilter Filter { get; set; } = new();
    public PagedList<LeadRow> Rows { get; set; } = null!;
    public List<SelectListItem> Courses { get; set; } = new();
    public DateOnly Today { get; set; }
}

public record BoardCard(int Id, string Name, string? Program, string? Phone, DateOnly InquiryDate, DateOnly? NextFollowUpDate, ProspectPhase Phase);

public class BoardModel
{
    public const int PerColumn = 60;
    public List<(ProspectPhase Phase, int Total, List<BoardCard> Cards)> Columns { get; set; } = new();
    public int? CourseId { get; set; }
    public string? Q { get; set; }
    public List<SelectListItem> Courses { get; set; } = new();
    public DateOnly Today { get; set; }
    public bool CanEdit { get; set; }
}

public record FollowUpRow(int Id, string Name, string? Phone, string? Email, string? Program, ProspectPhase Phase,
    DateOnly NextFollowUpDate, DateOnly InquiryDate, string? LastActivity, DateTime? LastActivityAt);

public class FollowUpsModel
{
    public List<FollowUpRow> Due { get; set; } = new();
    public int UpcomingWeek { get; set; }
    public DateOnly Today { get; set; }
    public bool CanEdit { get; set; }
}

/// <summary>The quick "log call + set next date" form on the follow-up list.</summary>
public class FollowUpLog
{
    public ActivityType Type { get; set; } = ActivityType.Call;
    [StringLength(4000)] public string? Body { get; set; }
    public DateOnly? NextFollowUpDate { get; set; }
    public ProspectPhase? Phase { get; set; }
    public string? ReturnTo { get; set; }
}

public class LeadDetailsModel
{
    public Lead Lead { get; set; } = null!;
    public string? AssignedTo { get; set; }
    public TimelineModel Timeline { get; set; } = null!;
    public List<(int Id, string Form, DateTime SubmittedAt, SubmissionStatus Status)> Submissions { get; set; } = new();
    public DateOnly Today { get; set; }
}

public class ConvertForm
{
    [Display(Name = "Program"), Required(ErrorMessage = "Choose the program.")] public int? CourseId { get; set; }
    [Display(Name = "Rank"), Required(ErrorMessage = "Choose the starting rank.")] public int? RankId { get; set; }
    [Display(Name = "Belt size"), Required(ErrorMessage = "Choose the belt size.")] public int? BeltSizeId { get; set; }
    [Display(Name = "Enrollment date"), Required(ErrorMessage = "Enter the enrollment date.")] public DateOnly? EnrollmentDate { get; set; }
    [Display(Name = "Monthly discount"), Range(0, 1_000_000, ErrorMessage = "The discount can't be negative.")] public decimal? Discount { get; set; } = 0;
}

public class ConvertModel
{
    public Lead Lead { get; set; } = null!;
    public ConvertForm Form { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public List<PersonMatch> ExistingStudents { get; set; } = new();
    public List<SelectListItem> Courses { get; set; } = new();
    public List<SelectListItem> Ranks { get; set; } = new();
    public List<SelectListItem> BeltSizes { get; set; } = new();
}

// ---------- Students ----------

public class StudentFilter
{
    public string? Q { get; set; }
    public int? CourseId { get; set; }
    public int? RankId { get; set; }
    /// <summary>"yes" (default), "no" or "all".</summary>
    public string? Active { get; set; }
    public AgeGroup? AgeGroup { get; set; }
    /// <summary>"name" (default), "number", "newest".</summary>
    public string? Sort { get; set; }

    public bool IsFiltered => !string.IsNullOrWhiteSpace(Q) || CourseId is not null || RankId is not null || AgeGroup is not null || (Active ?? "yes") != "yes";
}

public record StudentRow(int Id, string StudentNo, string Name, string? Phone, string? Email, string? City, DateOnly? DateOfBirth,
    string? Rank, string? RankColor, string? Program, DateOnly EnrollmentDate, bool IsActive);

public class StudentIndexModel
{
    public StudentFilter Filter { get; set; } = new();
    public PagedList<StudentRow> Rows { get; set; } = null!;
    public List<SelectListItem> Courses { get; set; } = new();
    public List<SelectListItem> Ranks { get; set; } = new();
    public DateOnly Today { get; set; }
}

public record CheckInRow(DateOnly Date, TimeOnly Time, string? Program, CheckInMethod Method, string? MarkedBy);
public record PromotionRow(DateOnly PromotedOn, string? From, string? FromColor, string To, string? ToColor, string? Notes);
public record MessageRow(DateTime CreatedAt, MessageChannel Channel, string To, string? Subject, MessageStatus Status);

public class StudentDetailsModel
{
    public Student Student { get; set; } = null!;
    public ProgramFees? Fees { get; set; }
    public DateOnly Today { get; set; }
    public TimelineModel Timeline { get; set; } = null!;

    public bool ShowAttendance { get; set; }
    public List<CheckInRow> CheckIns { get; set; } = new();
    public int CheckInsThisMonth { get; set; }
    public int CheckInsLast30 { get; set; }
    public DateOnly? LastSeen { get; set; }

    public List<PromotionRow> Promotions { get; set; } = new();

    public bool ShowBilling { get; set; }
    public decimal OpenBalance { get; set; }
    public int OpenInvoices { get; set; }
    public decimal OverdueBalance { get; set; }

    public bool ShowMessages { get; set; }
    public List<MessageRow> Messages { get; set; } = new();

    public (int Id, string Name)? FromLead { get; set; }
    public List<(int Id, string Form, DateTime SubmittedAt)> Submissions { get; set; } = new();
}
