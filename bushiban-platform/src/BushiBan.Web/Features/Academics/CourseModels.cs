using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Academics;

public record CourseRow(int Id, string Name, string? FeeStructure, decimal? MonthlyFee, List<string> RankingSystems, int WeeklyClasses, int ActiveStudents, bool IsActive);

public record ScheduleView(DayOfWeek Day, TimeOnly Start, int DurationMinutes, string? Instructor, bool IsActive)
{
    public string Times => $"{Format.Time(Start)} – {Format.Time(Start.AddMinutes(DurationMinutes))}";
}

public record EnrolledStudent(int Id, string Name, string StudentNo, string? Belt, string? BeltColor, DateOnly EnrolledOn);

public class CourseDetails
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int MinimumTermMonths { get; set; }
    public string? FeeStructure { get; set; }
    public decimal? AdmissionFee { get; set; }
    public decimal? SecurityFee { get; set; }
    public decimal? MonthlyFee { get; set; }
    public List<(int Id, string Name)> RankingSystems { get; set; } = new();
    public List<RankStepView> Path { get; set; } = new();
    public List<ScheduleView> Schedule { get; set; } = new();
    public List<EnrolledStudent> Students { get; set; } = new();
    public int Leads { get; set; }
}

public class ScheduleRowInput
{
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;
    public string? StartTime { get; set; }
    public int DurationMinutes { get; set; } = 60;
    [StringLength(80)] public string? Instructor { get; set; }
}

public class CourseForm
{
    public const string SameTime = "same";
    public const string PerDay = "perDay";

    public int Id { get; set; }
    [Required(ErrorMessage = "Give the program a name."), StringLength(120)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public int? FeeStructureId { get; set; }
    [Range(1, 120, ErrorMessage = "Minimum term must be between 1 and 120 months.")] public int MinimumTermMonths { get; set; } = 12;
    public bool IsActive { get; set; } = true;
    public List<int?> RankingSystemIds { get; set; } = new();

    /// <summary>"same": one start time and length on the ticked days. "perDay": a row per class.</summary>
    public string ScheduleMode { get; set; } = SameTime;
    public List<DayOfWeek> Days { get; set; } = new();
    public string? StartTime { get; set; } = "17:00";
    public int DurationMinutes { get; set; } = 60;
    [StringLength(80)] public string? Instructor { get; set; }
    public List<ScheduleRowInput> Rows { get; set; } = new();

    public List<SelectListItem> FeeStructures { get; set; } = new();
    public List<SelectListItem> RankingSystemOptions { get; set; } = new();

    /// <summary>Monday first, the way schools think about the week.</summary>
    public static readonly DayOfWeek[] Week =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };
}
