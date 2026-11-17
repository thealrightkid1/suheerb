using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain;

public enum Gender { Male = 1, Female = 2, Other = 3 }

public enum AgeGroup
{
    [Display(Name = "Pre-School")] PreSchool = 1,
    Kids = 2,
    Teens = 3,
    Adults = 4,
}

/// <summary>The sales pipeline used for leads (and kept on students for history). Same stages as the original CRM.</summary>
public enum ProspectPhase
{
    [Display(Name = "Hasn't Set Appointment")] NoAppointment = 1,
    [Display(Name = "Set Appointment")] AppointmentSet = 2,
    [Display(Name = "In Trial - Took First Class")] InTrial = 3,
    [Display(Name = "3rd Class Progress Check")] ProgressCheck = 4,
    [Display(Name = "Presented Program - Waiting for Commitment")] PresentedWaiting = 5,
    [Display(Name = "Presented Program - Denied Enrollment")] PresentedDenied = 6,
    Enrolled = 7,
    [Display(Name = "Stop Following Up")] StopFollowingUp = 8,
}

public enum LeadSource
{
    [Display(Name = "Walk-in")] WalkIn = 1,
    Phone = 2,
    Website = 3,
    [Display(Name = "Social media")] SocialMedia = 4,
    Event = 5,
    Referral = 6,
    [Display(Name = "Registration kiosk")] Kiosk = 7,
    Other = 99,
}

public enum ActivityType
{
    Note = 1,
    Call = 2,
    Email = 3,
    SMS = 4,
    Meeting = 5,
    [Display(Name = "Follow-up")] FollowUp = 6,
    [Display(Name = "Stage change")] StageChange = 7,
    System = 8,
}

public enum CheckInMethod
{
    Manual = 1,
    [Display(Name = "Kiosk search")] KioskSearch = 2,
    [Display(Name = "Kiosk PIN")] KioskPin = 3,
    [Display(Name = "QR code")] QrCode = 4,
    [Display(Name = "Face recognition")] FaceRecognition = 5,
}

public enum FormCategory { Admission = 1, Agreement = 2, ChangeForm = 3, Generic = 4 }

public enum SubmissionStatus
{
    New = 1,
    Reviewed = 2,
    [Display(Name = "Linked")] Linked = 3,
    Archived = 4,
}

public enum AccountType { Assets = 1, Liabilities = 2, Equity = 3, Revenue = 4, Expenses = 5 }

public enum VoucherType
{
    Receipt = 1,
    Payment = 2,
    Contra = 3,
    Journal = 4,
    [Display(Name = "Fee invoice")] FeeInvoice = 5,
}

public enum VoucherStatus { Draft = 1, Posted = 2, Reversed = 3 }

public enum ContractStatus { Active = 1, Completed = 2, Cancelled = 3, OnHold = 4 }

public enum InvoiceStatus
{
    Open = 1,
    [Display(Name = "Partially paid")] PartiallyPaid = 2,
    Paid = 3,
    Void = 4,
}

public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    [Display(Name = "EFT / ACH")] Eft = 3,
    Check = 4,
    [Display(Name = "Educational Funding Co.")] Efc = 5,
    Other = 9,
}

public enum MessageChannel { Email = 1, SMS = 2 }

public enum MessageStatus
{
    Queued = 1,
    Sent = 2,
    Failed = 3,
    [Display(Name = "Not sent: provider not set up")] NotConfigured = 4,
}

public enum EventCategory { Personal = 1, Business = 2, Family = 3, Holiday = 4, Class = 5, Event = 6, Other = 9 }
