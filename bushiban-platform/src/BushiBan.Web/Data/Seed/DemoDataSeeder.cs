using BushiBan.Web.Domain;
using BushiBan.Web.Domain.Attendance;
using BushiBan.Web.Domain.People;
using BushiBan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Data.Seed;

/// <summary>
/// Development only: fills the Pasadena and Demo branches with made-up students, leads and attendance
/// so every screen has something to show. Never runs unless Seed:DemoData is true.
/// </summary>
public class DemoDataSeeder
{
    private static readonly string[] First = { "Liam", "Olivia", "Noah", "Emma", "Mateo", "Ava", "Elijah", "Sophia", "Lucas", "Mia", "Ethan", "Isabella", "Aiden", "Camila", "Diego", "Harper", "Jayden", "Luna", "Caleb", "Aria", "Owen", "Zoe", "Isaiah", "Nora", "Gabriel", "Layla", "Ryan", "Chloe", "Adrian", "Ella" };
    private static readonly string[] Last = { "Garcia", "Johnson", "Nguyen", "Martinez", "Smith", "Brown", "Lopez", "Davis", "Hernandez", "Wilson", "Patel", "Anderson", "Thomas", "Moore", "Jackson", "Lee", "Perez", "Walker", "Young", "King" };

    private readonly AppDbContext _db;
    private readonly ICurrentBranch _branch;
    private readonly NumberSequenceService _numbers;

    public DemoDataSeeder(AppDbContext db, ICurrentBranch branch, NumberSequenceService numbers) { _db = db; _branch = branch; _numbers = numbers; }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var code in new[] { "PAS", "DEMO" })
        {
            var branch = await _db.Branches.Include(b => b.City).FirstOrDefaultAsync(b => b.Code == code, ct);
            if (branch is null) continue;
            using var _ = _branch.Override(branch.Id);
            if (await _db.Students.AnyAsync(ct) || await _db.Leads.AnyAsync(ct)) continue;

            var rnd = new Random(branch.Id * 7919);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var courses = await _db.Courses.ToListAsync(ct);
            var ranks = await _db.RankingSystems.Where(s => s.Name == "Standard System")
                .SelectMany(s => s.Ranks).OrderBy(r => r.Index).Select(r => r.RankId).ToListAsync(ct);
            var belts = await _db.BeltSizes.Select(b => b.Id).ToListAsync(ct);
            string Pick(string[] a) => a[rnd.Next(a.Length)];
            string Phone() => $"(713) 555-{rnd.Next(1000, 9999)}";

            var students = new List<Student>();
            for (var i = 0; i < 28; i++)
            {
                var first = Pick(First); var last = Pick(Last);
                var dob = today.AddYears(-rnd.Next(4, 45)).AddDays(-rnd.Next(0, 365));
                var age = today.Year - dob.Year;
                var enrolled = today.AddDays(-rnd.Next(10, 600));
                var rankIndex = Math.Min(ranks.Count - 1, Math.Max(0, (today.DayNumber - enrolled.DayNumber) / 95 + rnd.Next(-1, 2)));
                var course = courses.FirstOrDefault(c => c.Name == (age < 6 ? "Pre-K Martial Arts" : age < 16 ? "Youth Martial Arts" : rnd.Next(4) == 0 ? "MMA" : "Adult Martial Arts")) ?? courses[0];
                students.Add(new Student
                {
                    BranchId = branch.Id,
                    StudentNo = await _numbers.NextStudentNoAsync(branch.Id, branch.Code, ct),
                    FirstName = first, LastName = last, DateOfBirth = dob,
                    AgeGroup = age < 6 ? AgeGroup.PreSchool : age < 13 ? AgeGroup.Kids : age < 18 ? AgeGroup.Teens : AgeGroup.Adults,
                    Gender = rnd.Next(2) == 0 ? Gender.Male : Gender.Female,
                    FatherName = age < 18 ? $"{Pick(First)} {last}" : null,
                    Phone1Label = "Mobile", Phone1 = Phone(), Email1Label = "Personal",
                    Email1 = $"{first}.{last}{rnd.Next(10, 99)}@example.com".ToLowerInvariant(),
                    Address1 = $"{rnd.Next(100, 9999)} {Pick(new[] { "Spencer Hwy", "Fairmont Pkwy", "Red Bluff Rd", "Center St", "Burke Rd" })}",
                    CityId = branch.CityId, ZipCode = branch.ZipCode,
                    CourseId = course.Id, RankId = ranks.Count > 0 ? ranks[rankIndex] : null,
                    RankSince = today.AddDays(-rnd.Next(5, 120)),
                    BeltSizeId = belts.Count > 0 ? belts[Math.Min(belts.Count - 1, age / 5)] : null,
                    EnrollmentDate = enrolled, ProspectPhase = ProspectPhase.Enrolled,
                    KioskPin = (1000 + i * 37 + branch.Id).ToString(),
                    IsActive = rnd.Next(10) != 0,
                    MarketingSource = Pick(new[] { "Google", "Facebook", "Friend referral", "Walk-in", "School flyer" }),
                });
            }
            _db.Students.AddRange(students);
            await _db.SaveChangesAsync(ct);

            var phases = Enum.GetValues<ProspectPhase>().Where(p => p != ProspectPhase.Enrolled).ToArray();
            for (var i = 0; i < 22; i++)
            {
                var first = Pick(First); var last = Pick(Last);
                _db.Leads.Add(new Lead
                {
                    BranchId = branch.Id, FirstName = first, LastName = last,
                    DateOfBirth = today.AddYears(-rnd.Next(4, 40)),
                    Phone1Label = "Mobile", Phone1 = Phone(), Email1 = $"{first}.{last}@example.com".ToLowerInvariant(),
                    CityId = branch.CityId, ZipCode = branch.ZipCode,
                    CourseId = courses[rnd.Next(courses.Count)].Id,
                    InquiryDate = today.AddDays(-rnd.Next(0, 90)),
                    ProspectPhase = phases[rnd.Next(phases.Length)],
                    Source = (LeadSource)new[] { 1, 2, 3, 4, 5, 6, 7 }[rnd.Next(7)],
                    NextFollowUpDate = rnd.Next(3) == 0 ? null : today.AddDays(rnd.Next(-5, 10)),
                    MarketingSource = Pick(new[] { "Google", "Instagram", "Facebook", "Friend referral", "Community event" }),
                });
            }
            await _db.SaveChangesAsync(ct);

            // Attendance for the last 45 days, mostly on class days.
            foreach (var s in students.Where(s => s.IsActive))
            {
                var perWeek = rnd.Next(1, 4);
                for (var d = 45; d >= 1; d--)
                {
                    var date = today.AddDays(-d);
                    if (date.DayOfWeek == DayOfWeek.Sunday || rnd.Next(6) >= perWeek * 1.4) continue;
                    _db.AttendanceRecords.Add(new AttendanceRecord
                    {
                        BranchId = branch.Id, StudentId = s.Id, CourseId = s.CourseId, Date = date,
                        CheckInTime = new TimeOnly(rnd.Next(16, 20), rnd.Next(0, 4) * 15),
                        Method = rnd.Next(3) == 0 ? CheckInMethod.Manual : CheckInMethod.KioskPin, MarkedBy = "demo",
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }
    }
}
