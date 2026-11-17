using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Attendance;

public class AttendanceRecord : BranchEntity
{
    public int StudentId { get; set; }
    public People.Student? Student { get; set; }
    public int? CourseId { get; set; }
    public Academics.Course? Course { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly CheckInTime { get; set; }
    public CheckInMethod Method { get; set; } = CheckInMethod.Manual;
    /// <summary>Who recorded it (user name), or the kiosk account.</summary>
    [StringLength(150)] public string? MarkedBy { get; set; }
    /// <summary>Match confidence for automated methods such as face recognition (0–100).</summary>
    public decimal? Confidence { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
}
