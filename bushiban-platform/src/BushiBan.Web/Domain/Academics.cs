using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Academics;

public class Rank : BranchEntity
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    /// <summary>Belt colour for badges, e.g. #ffffff. Optional.</summary>
    [StringLength(20)] public string? Color { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>An ordered belt progression (e.g. "Standard System": White → … → 10th Dan).</summary>
public class RankingSystem : BranchEntity
{
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RankingSystemRank> Ranks { get; set; } = new();
}

public class RankingSystemRank : Entity
{
    public int RankingSystemId { get; set; }
    public RankingSystem? RankingSystem { get; set; }
    public int RankId { get; set; }
    public Rank? Rank { get; set; }
    /// <summary>Position in the progression, starting at 1.</summary>
    public int Index { get; set; }
    /// <summary>Minimum days a student stays at this rank before testing for the next one.</summary>
    public int DurationInDays { get; set; }
    /// <summary>New: minimum classes attended at this rank before testing (0 = no minimum).</summary>
    public int MinimumClasses { get; set; }
}

public class BeltSize : BranchEntity
{
    [Required, StringLength(60)] public string Name { get; set; } = "";
    [Range(1, 500)] public decimal SizeInInch { get; set; }
    public bool IsActive { get; set; } = true;
}

public class FeeStructure : BranchEntity
{
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [Range(0, 1_000_000)] public decimal AdmissionFee { get; set; }
    [Range(0, 1_000_000)] public decimal SecurityFee { get; set; }
    [Range(0, 1_000_000)] public decimal MonthlyFee { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Shown as "Program" in the app (Youth Martial Arts, MMA, …).</summary>
public class Course : BranchEntity
{
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    public int? FeeStructureId { get; set; }
    public FeeStructure? FeeStructure { get; set; }
    /// <summary>Minimum contract length in months (Pre-K is 6, others 12).</summary>
    public int MinimumTermMonths { get; set; } = 12;
    public bool IsActive { get; set; } = true;
    public List<CourseRankingSystem> RankingSystems { get; set; } = new();
    public List<ClassSchedule> Schedule { get; set; } = new();
}

public class CourseRankingSystem : Entity
{
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public int RankingSystemId { get; set; }
    public RankingSystem? RankingSystem { get; set; }
    public int Index { get; set; }
}

public class ClassSchedule : Entity
{
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    [Range(5, 600)] public int DurationMinutes { get; set; } = 60;
    [StringLength(80)] public string? Instructor { get; set; }
    public bool IsActive { get; set; } = true;
}
