using BushiBan.Web.Data;
using BushiBan.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace BushiBan.Web.Features.Communications;

/// <summary>One item in the FullCalendar JSON feed.</summary>
public sealed record CalendarItem(
    string Id, string Title, string Start, string? End, bool AllDay, string Color, bool Editable,
    string Kind, EventCategory Category, string? Location, string? Description, int? EventId);

/// <summary>
/// Builds the calendar for a date range: saved events, weekly class times from each active program's schedule,
/// and (optionally) student birthdays.
///
/// TIME ZONES: everything on the calendar is branch-local wall-clock time. <see cref="Domain.Communication.CalendarEvent.Start"/>
/// and End are stored as the branch's local time (DateTimeKind.Unspecified), not UTC, so "Belt test at 6 pm" stays at 6 pm
/// whoever looks at it and across daylight-saving changes. The page runs FullCalendar in its "UTC" mode purely as a
/// "no conversion" mode, and the feed sends times without an offset.
/// </summary>
public class CalendarFeed
{
    /// <summary>The longest range one request may ask for (FullCalendar's month view asks for about 6 weeks).</summary>
    public const int MaxRangeDays = 100;

    public static readonly IReadOnlyDictionary<EventCategory, string> Colors = new Dictionary<EventCategory, string>
    {
        [EventCategory.Business] = "#2563eb",
        [EventCategory.Event] = "#ea580c",
        [EventCategory.Holiday] = "#c8102e",
        [EventCategory.Personal] = "#7c3aed",
        [EventCategory.Family] = "#16a34a",
        [EventCategory.Class] = "#64748b",
        [EventCategory.Other] = "#0f766e",
    };
    public const string BirthdayColor = "#db2777";

    /// <summary>Categories staff can choose for their own events (Class is generated from program schedules).</summary>
    public static readonly EventCategory[] Choosable = Enum.GetValues<EventCategory>().Where(c => c != EventCategory.Class).ToArray();

    private readonly AppDbContext _db;
    public CalendarFeed(AppDbContext db) => _db = db;

    public async Task<List<CalendarItem>> ItemsAsync(DateTime start, DateTime end, bool classes, bool birthdays, bool canEdit, CancellationToken ct = default)
    {
        if (end <= start) return new();
        if ((end - start).TotalDays > MaxRangeDays) end = start.AddDays(MaxRangeDays);

        var items = new List<CalendarItem>();

        var events = await _db.CalendarEvents
            .Where(e => e.Start < end && (e.End ?? e.Start) >= start)
            .OrderBy(e => e.Start).ToListAsync(ct);
        items.AddRange(events.Select(e => new CalendarItem(
            "e" + e.Id, e.Title, Iso(e.Start, e.AllDay), e.End is { } en ? Iso(en, e.AllDay) : null, e.AllDay,
            Colors.GetValueOrDefault(e.Category, Colors[EventCategory.Other]), canEdit, "event", e.Category, e.Location, e.Description, e.Id)));

        if (classes) items.AddRange(await ClassOccurrencesAsync(start, end, ct));
        if (birthdays) items.AddRange(await BirthdaysAsync(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), ct));
        return items;
    }

    /// <summary>Every class time from the weekly schedules of active programs that falls in [start, end).</summary>
    public async Task<List<CalendarItem>> ClassOccurrencesAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        // Join to Courses (branch-scoped) because schedule rows have no branch of their own.
        var slots = await (
            from s in _db.ClassSchedules
            join c in _db.Courses on s.CourseId equals c.Id
            where s.IsActive && c.IsActive
            select new { s.Id, s.DayOfWeek, s.StartTime, s.DurationMinutes, s.Instructor, Course = c.Name })
            .ToListAsync(ct);
        var byDay = slots.ToLookup(s => s.DayOfWeek);
        var items = new List<CalendarItem>();
        for (var day = DateOnly.FromDateTime(start); day.ToDateTime(TimeOnly.MinValue) < end; day = day.AddDays(1))
        {
            foreach (var s in byDay[day.DayOfWeek].OrderBy(s => s.StartTime))
            {
                var from = day.ToDateTime(s.StartTime);
                var to = from.AddMinutes(s.DurationMinutes);
                if (to <= start || from >= end) continue;
                items.Add(new CalendarItem($"c{s.Id}-{day:yyyyMMdd}", s.Course, Iso(from, false), Iso(to, false), false,
                    Colors[EventCategory.Class], false, "class", EventCategory.Class, null,
                    s.Instructor is null ? "Weekly class" : $"Weekly class with {s.Instructor}", null));
            }
        }
        return items;
    }

    private async Task<List<CalendarItem>> BirthdaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var students = await _db.Students.Where(s => s.IsActive && s.DateOfBirth != null)
            .Select(s => new { s.Id, s.FirstName, s.LastName, Dob = s.DateOfBirth!.Value }).ToListAsync(ct);
        var items = new List<CalendarItem>();
        foreach (var s in students)
        {
            for (var year = from.Year; year <= to.Year; year++)
            {
                // Feb 29 birthdays are shown on Feb 28 in other years.
                var day = Math.Min(s.Dob.Day, DateTime.DaysInMonth(year, s.Dob.Month));
                var date = new DateOnly(year, s.Dob.Month, day);
                if (date < from || date >= to) continue;
                var age = year - s.Dob.Year;
                items.Add(new CalendarItem($"b{s.Id}-{year}", $"{s.FirstName} {s.LastName}'s birthday ({age})", date.ToString("yyyy-MM-dd"), null, true,
                    BirthdayColor, false, "birthday", EventCategory.Personal, null, $"{s.FirstName} turns {age}.", null));
            }
        }
        return items;
    }

    private static string Iso(DateTime d, bool allDay) => allDay ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-ddTHH:mm:ss");
}
