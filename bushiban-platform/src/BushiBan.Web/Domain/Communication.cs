using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Communication;

/// <summary>Every email/SMS sent (or attempted), linked to the lead or student it was about.</summary>
public class MessageLog : BranchEntity
{
    public MessageChannel Channel { get; set; }
    [Required, StringLength(200)] public string To { get; set; } = "";
    [StringLength(300)] public string? Subject { get; set; }
    [Required] public string Body { get; set; } = "";
    public MessageStatus Status { get; set; } = MessageStatus.Queued;
    [StringLength(1000)] public string? Error { get; set; }
    public DateTime? SentAt { get; set; }
    public int? StudentId { get; set; }
    public People.Student? Student { get; set; }
    public int? LeadId { get; set; }
    public People.Lead? Lead { get; set; }
    /// <summary>Groups messages from one bulk send.</summary>
    public Guid? BatchId { get; set; }
}

public class MessageTemplate : BranchEntity
{
    [Required, StringLength(120)] public string Name { get; set; } = "";
    public MessageChannel Channel { get; set; }
    [StringLength(300)] public string? Subject { get; set; }
    /// <summary>Supports {{FirstName}}, {{LastName}}, {{BranchName}}, {{Program}}, {{Balance}}.</summary>
    [Required] public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class CalendarEvent : BranchEntity
{
    [Required, StringLength(200)] public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public bool AllDay { get; set; }
    public EventCategory Category { get; set; } = EventCategory.Business;
    [StringLength(2000)] public string? Description { get; set; }
    [StringLength(200)] public string? Location { get; set; }
}
