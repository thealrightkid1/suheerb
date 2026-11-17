using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Domain;
using BushiBan.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Attendance;

public record ClassSlot(int CourseId, string Program, DayOfWeek Day, TimeOnly Start, int DurationMinutes, string? Instructor)
{
    public TimeOnly End => Start.AddMinutes(DurationMinutes);
    public string Times => $"{Format.Time(Start)} – {Format.Time(End)}";
}

public class RollStudent
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string StudentNo { get; init; } = "";
    public string? Belt { get; init; }
    public string? BeltColor { get; init; }
    public bool Present { get; init; }
    /// <summary>Checked in by someone else and the user can't delete attendance, so it can't be unticked here.</summary>
    public bool Locked { get; init; }
    /// <summary>Not enrolled in this program (added from another program).</summary>
    public bool Visitor { get; init; }
    public string? CheckedInAt { get; init; }
}

public class RollPage
{
    public int? CourseId { get; set; }
    public string? CourseName { get; set; }
    public DateOnly Date { get; set; }
    public bool IsToday { get; set; }
    public string CheckInTime { get; set; } = "";
    public List<SelectListItem> Courses { get; set; } = new();
    public List<ClassSlot> Classes { get; set; } = new();
    public List<RollStudent> Roster { get; set; } = new();
    public List<SelectListItem> OtherStudents { get; set; } = new();
}

public class AttendanceFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Q { get; set; }
    public int? CourseId { get; set; }
    public CheckInMethod? Method { get; set; }
    public int Page { get; set; } = 1;
}

public record AttendanceRow(int Id, int StudentId, string StudentName, string StudentNo, DateOnly Date, TimeOnly CheckInTime,
    string? Program, CheckInMethod Method, string? MarkedBy, decimal? Confidence, string? Notes);

public class AttendanceIndexPage
{
    public AttendanceFilter Filter { get; set; } = new();
    public PagedList<AttendanceRow> Rows { get; set; } = new(Array.Empty<AttendanceRow>(), 0, 1, 50);
    public List<SelectListItem> Courses { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
}

public class AttendanceEditModel
{
    public int Id { get; set; }
    public int StudentId { get; set; }
    public string StudentName { get; set; } = "";
    [Required(ErrorMessage = "Pick a date.")] public DateOnly? Date { get; set; }
    [Required(ErrorMessage = "Enter the check-in time.")] public string? CheckInTime { get; set; }
    public int? CourseId { get; set; }
    public CheckInMethod Method { get; set; } = CheckInMethod.Manual;
    [StringLength(500)] public string? Notes { get; set; }
    public decimal? Confidence { get; set; }
    public string? MarkedBy { get; set; }
    public List<SelectListItem> Courses { get; set; } = new();
}

public record StudentSummaryRow(int StudentId, string Name, string StudentNo, string? Program, string? Belt, string? BeltColor,
    int Classes, DateOnly? LastInPeriod, DateOnly? LastSeen);

public record AbsentRow(int StudentId, string Name, string StudentNo, string? Program, string? Phone, DateOnly? LastSeen, int? DaysAway, DateOnly EnrolledOn);

public class AttendanceReportPage
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public int? CourseId { get; set; }
    public int Days { get; set; } = 14;
    public DateOnly Today { get; set; }
    public List<SelectListItem> Courses { get; set; } = new();
    public List<StudentSummaryRow> Summary { get; set; } = new();
    public List<AbsentRow> Absent { get; set; } = new();
    public int TotalClasses => Summary.Sum(s => s.Classes);
    public bool CanMessage { get; set; }
}
