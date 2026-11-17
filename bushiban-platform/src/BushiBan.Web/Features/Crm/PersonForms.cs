using System.ComponentModel.DataAnnotations;
using BushiBan.Web.Domain;
using BushiBan.Web.Domain.People;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BushiBan.Web.Features.Crm;

/// <summary>
/// The fields leads and students share (PersonBase), as posted by the editor.
/// Bound instead of the entity so a crafted post can't set BranchId, StudentNo, audit fields and so on.
/// </summary>
public abstract class PersonForm
{
    public int Id { get; set; }

    // Personal details
    [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    [Required(ErrorMessage = "Choose a title."), StringLength(20)] public string? Title { get; set; }
    [Display(Name = "First name"), Required(ErrorMessage = "Enter the first name."), StringLength(80)] public string? FirstName { get; set; }
    [Display(Name = "Last name"), Required(ErrorMessage = "Enter the last name."), StringLength(80)] public string? LastName { get; set; }
    [Display(Name = "Father's / guardian's name"), StringLength(120)] public string? FatherName { get; set; }
    [Display(Name = "Mother's name"), StringLength(120)] public string? MotherName { get; set; }
    [Display(Name = "Date of birth")] public DateOnly? DateOfBirth { get; set; }
    [Display(Name = "Age group")] public AgeGroup? AgeGroup { get; set; }
    public Gender? Gender { get; set; }
    [Display(Name = "Belt size")] public int? BeltSizeId { get; set; }

    // Contact & address
    [StringLength(30)] public string? Phone1Label { get; set; } = "Mobile";
    [Display(Name = "Phone 1"), StringLength(30), Phone(ErrorMessage = "Enter a valid phone number.")] public string? Phone1 { get; set; }
    [StringLength(30)] public string? Phone2Label { get; set; }
    [Display(Name = "Phone 2"), StringLength(30), Phone(ErrorMessage = "Enter a valid phone number.")] public string? Phone2 { get; set; }
    [StringLength(30)] public string? Email1Label { get; set; } = "Personal";
    [Display(Name = "Email 1"), StringLength(150), EmailAddress(ErrorMessage = "Enter a valid email address.")] public string? Email1 { get; set; }
    [StringLength(30)] public string? Email2Label { get; set; }
    [Display(Name = "Email 2"), StringLength(150), EmailAddress(ErrorMessage = "Enter a valid email address.")] public string? Email2 { get; set; }
    [Display(Name = "Address line 1"), StringLength(200)] public string? Address1 { get; set; }
    [Display(Name = "Address line 2"), StringLength(200)] public string? Address2 { get; set; }
    [Display(Name = "City")] public int? CityId { get; set; }
    [Display(Name = "Zip code"), StringLength(20)] public string? ZipCode { get; set; }

    // Program & rank
    [Display(Name = "Program")] public int? CourseId { get; set; }
    [Display(Name = "Rank")] public int? RankId { get; set; }

    // Notes & source
    [Display(Name = "Medical concerns"), StringLength(2000)] public string? MedicalConcerns { get; set; }
    [Display(Name = "Additional information"), StringLength(4000)] public string? AdditionalInformation { get; set; }
    [Display(Name = "Marketing source"), StringLength(120)] public string? MarketingSource { get; set; }
    [Display(Name = "Marketing source detail"), StringLength(300)] public string? MarketingSourceDetail { get; set; }
    [StringLength(120)] public string? Meeting { get; set; }
    [Display(Name = "Meeting detail"), StringLength(300)] public string? MeetingDetail { get; set; }

    // Social media
    [StringLength(200)] public string? Facebook { get; set; }
    [StringLength(200)] public string? Instagram { get; set; }
    [Display(Name = "LinkedIn"), StringLength(200)] public string? LinkedIn { get; set; }
    [StringLength(200)] public string? Pinterest { get; set; }
    [Display(Name = "X / Twitter"), StringLength(200)] public string? Twitter { get; set; }
    [StringLength(200)] public string? Website { get; set; }

    /// <summary>Set by the "Save anyway" button after a possible-duplicate warning.</summary>
    public bool IgnoreDuplicates { get; set; }

    /// <summary>Dropdown data and warnings for the view. Never bound from the request.</summary>
    [BindNever, ValidateNever] public PersonFormOptions Options { get; set; } = new();

    public abstract bool IsStudent { get; }

    public void CopyFrom(PersonBase p)
    {
        Id = p.Id; IsActive = p.IsActive; Title = p.Title; FirstName = p.FirstName; LastName = p.LastName;
        FatherName = p.FatherName; MotherName = p.MotherName; DateOfBirth = p.DateOfBirth; AgeGroup = p.AgeGroup;
        Gender = p.Gender; BeltSizeId = p.BeltSizeId;
        Phone1Label = p.Phone1Label; Phone1 = p.Phone1; Phone2Label = p.Phone2Label; Phone2 = p.Phone2;
        Email1Label = p.Email1Label; Email1 = p.Email1; Email2Label = p.Email2Label; Email2 = p.Email2;
        Address1 = p.Address1; Address2 = p.Address2; CityId = p.CityId; ZipCode = p.ZipCode;
        CourseId = p.CourseId; RankId = p.RankId;
        MedicalConcerns = p.MedicalConcerns; AdditionalInformation = p.AdditionalInformation;
        MarketingSource = p.MarketingSource; MarketingSourceDetail = p.MarketingSourceDetail;
        Meeting = p.Meeting; MeetingDetail = p.MeetingDetail;
        Facebook = p.Facebook; Instagram = p.Instagram; LinkedIn = p.LinkedIn; Pinterest = p.Pinterest; Twitter = p.Twitter; Website = p.Website;
    }

    /// <summary>Writes the posted values onto the entity (trimmed; blanks become null).</summary>
    public void ApplyTo(PersonBase p, DateOnly today)
    {
        p.IsActive = IsActive; p.Title = T(Title); p.FirstName = T(FirstName) ?? ""; p.LastName = T(LastName) ?? "";
        p.FatherName = T(FatherName); p.MotherName = T(MotherName); p.DateOfBirth = DateOfBirth;
        p.AgeGroup = AgeGroup ?? PeopleService.SuggestAgeGroup(DateOfBirth, today);
        p.Gender = Gender; p.BeltSizeId = BeltSizeId;
        p.Phone1Label = T(Phone1Label); p.Phone1 = T(Phone1); p.Phone2Label = T(Phone2Label); p.Phone2 = T(Phone2);
        p.Email1Label = T(Email1Label); p.Email1 = T(Email1); p.Email2Label = T(Email2Label); p.Email2 = T(Email2);
        p.Address1 = T(Address1); p.Address2 = T(Address2); p.CityId = CityId; p.ZipCode = T(ZipCode);
        p.CourseId = CourseId; p.RankId = RankId;
        p.MedicalConcerns = T(MedicalConcerns); p.AdditionalInformation = T(AdditionalInformation);
        p.MarketingSource = T(MarketingSource); p.MarketingSourceDetail = T(MarketingSourceDetail);
        p.Meeting = T(Meeting); p.MeetingDetail = T(MeetingDetail);
        p.Facebook = T(Facebook); p.Instagram = T(Instagram); p.LinkedIn = T(LinkedIn); p.Pinterest = T(Pinterest);
        p.Twitter = T(Twitter); p.Website = T(Website);
    }

    protected static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public class LeadForm : PersonForm
{
    [Display(Name = "Inquiry date"), Required(ErrorMessage = "Enter the inquiry date.")] public DateOnly? InquiryDate { get; set; }
    [Display(Name = "Prospect phase"), Required(ErrorMessage = "Choose the prospect phase.")] public ProspectPhase? ProspectPhase { get; set; } = Domain.ProspectPhase.NoAppointment;
    [Display(Name = "Lead source")] public LeadSource Source { get; set; } = LeadSource.WalkIn;
    [Display(Name = "Next follow-up")] public DateOnly? NextFollowUpDate { get; set; }
    [Display(Name = "Assigned to")] public string? AssignedToUserId { get; set; }

    public override bool IsStudent => false;

    public static LeadForm From(Lead l)
    {
        var f = new LeadForm
        {
            InquiryDate = l.InquiryDate, ProspectPhase = l.ProspectPhase, Source = l.Source,
            NextFollowUpDate = l.NextFollowUpDate, AssignedToUserId = l.AssignedToUserId,
        };
        f.CopyFrom(l);
        return f;
    }

    public void ApplyTo(Lead l, DateOnly today)
    {
        base.ApplyTo(l, today);
        l.InquiryDate = InquiryDate ?? today;
        l.ProspectPhase = ProspectPhase ?? Domain.ProspectPhase.NoAppointment;
        l.Source = Source;
        l.NextFollowUpDate = NextFollowUpDate;
        l.AssignedToUserId = string.IsNullOrWhiteSpace(AssignedToUserId) ? null : AssignedToUserId;
    }
}

public class StudentForm : PersonForm
{
    /// <summary>Shown read-only; assigned automatically on create.</summary>
    [BindNever] public string? StudentNo { get; set; }
    [Display(Name = "Enrollment date"), Required(ErrorMessage = "Enter the enrollment date.")] public DateOnly? EnrollmentDate { get; set; }
    [Display(Name = "Monthly discount"), Required(ErrorMessage = "Enter a discount (0 for none)."), Range(0, 1_000_000, ErrorMessage = "The discount can't be negative.")]
    public decimal? Discount { get; set; } = 0;
    [Display(Name = "At this rank since")] public DateOnly? RankSince { get; set; }

    [Display(Name = "Email 1")] public bool IsEmail1Subscriber { get; set; } = true;
    [Display(Name = "Email 2")] public bool IsEmail2Subscriber { get; set; }
    [Display(Name = "Phone 1")] public bool IsPhone1Subscriber { get; set; } = true;
    [Display(Name = "Phone 2")] public bool IsPhone2Subscriber { get; set; }
    [Display(Name = "Postcard")] public bool IsPostCardSubscriber { get; set; }

    /// <summary>When enrolling from a lead or a kiosk registration (create only).</summary>
    public int? LeadId { get; set; }
    public int? SubmissionId { get; set; }

    public override bool IsStudent => true;

    public static StudentForm From(Student s)
    {
        var f = new StudentForm
        {
            StudentNo = s.StudentNo, EnrollmentDate = s.EnrollmentDate, Discount = s.Discount, RankSince = s.RankSince,
            IsEmail1Subscriber = s.IsEmail1Subscriber, IsEmail2Subscriber = s.IsEmail2Subscriber,
            IsPhone1Subscriber = s.IsPhone1Subscriber, IsPhone2Subscriber = s.IsPhone2Subscriber, IsPostCardSubscriber = s.IsPostCardSubscriber,
            LeadId = s.LeadId,
        };
        f.CopyFrom(s);
        return f;
    }

    public void ApplyTo(Student s, DateOnly today)
    {
        var (oldRank, oldSince) = (s.RankId, s.RankSince);
        base.ApplyTo(s, today);
        s.EnrollmentDate = EnrollmentDate ?? today;
        s.Discount = Discount ?? 0;
        if (s.Id == 0) s.RankSince = RankId is null ? null : RankSince ?? s.EnrollmentDate;
        // A new rank without a new "since" date starts today.
        else if (oldRank != RankId && (RankSince is null || RankSince == oldSince)) s.RankSince = RankId is null ? null : today;
        else s.RankSince = RankSince ?? oldSince;
        s.IsEmail1Subscriber = IsEmail1Subscriber; s.IsEmail2Subscriber = IsEmail2Subscriber;
        s.IsPhone1Subscriber = IsPhone1Subscriber; s.IsPhone2Subscriber = IsPhone2Subscriber; s.IsPostCardSubscriber = IsPostCardSubscriber;
    }
}

/// <summary>Dropdowns and extra information the editor needs.</summary>
public class PersonFormOptions
{
    public static readonly string[] Titles = { "Mr.", "Mrs.", "Ms.", "Miss", "Master", "Dr." };
    public static readonly string[] PhoneLabels = { "Mobile", "Home", "Work", "Parent", "Other" };
    public static readonly string[] EmailLabels = { "Personal", "Work", "Parent", "Other" };
    public static readonly string[] MarketingSources = { "Google", "Facebook", "Instagram", "Friend referral", "Walk-in", "School flyer", "Community event", "Website", "Other" };

    public List<SelectListItem> Courses { get; set; } = new();
    public List<SelectListItem> Ranks { get; set; } = new();
    public List<SelectListItem> BeltSizes { get; set; } = new();
    public List<SelectListItem> Cities { get; set; } = new();
    public List<SelectListItem> Staff { get; set; } = new();
    /// <summary>Program id → standard fees, for the read-only fee panel on the student form.</summary>
    public Dictionary<int, ProgramFees> Fees { get; set; } = new();
    /// <summary>Possible duplicates found when creating. Shown as a warning with a "Save anyway" button.</summary>
    public List<PersonMatch> Duplicates { get; set; } = new();
    /// <summary>Where the pre-filled values came from, e.g. "lead Jane Doe" (create only).</summary>
    public string? PrefilledFrom { get; set; }
}

public record ProgramFees(string Name, decimal Admission, decimal Security, decimal Monthly);

/// <summary>A lead or student that looks like the same person (same phone digits or email).</summary>
public record PersonMatch(string Kind, int Id, string Name, string? Phone, string? Email, string? Detail);
