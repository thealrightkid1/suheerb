using System.ComponentModel.DataAnnotations;

namespace BushiBan.Web.Domain.Organization;

public class Country : AuditableEntity
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<State> States { get; set; } = new();
}

public class State : AuditableEntity
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public int CountryId { get; set; }
    public Country? Country { get; set; }
    public bool IsActive { get; set; } = true;
    public List<City> Cities { get; set; } = new();
}

public class City : AuditableEntity
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    public int StateId { get; set; }
    public State? State { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A school groups branches (e.g. "Bushi Ban International").</summary>
public class School : AuditableEntity
{
    [Required, StringLength(150)] public string Name { get; set; } = "";
    [StringLength(150)] public string? LegalName { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Branch : AuditableEntity
{
    [Required, StringLength(150)] public string Name { get; set; } = "";
    public int SchoolId { get; set; }
    public School? School { get; set; }
    [StringLength(200)] public string? Address { get; set; }
    public int? CityId { get; set; }
    public City? City { get; set; }
    [StringLength(20)] public string? ZipCode { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(150)] public string? Email { get; set; }
    /// <summary>IANA time zone, e.g. America/Chicago. Used for "today" in attendance and billing.</summary>
    [Required, StringLength(60)] public string TimeZone { get; set; } = "America/Chicago";
    /// <summary>Prefix for student numbers, e.g. "PAS" gives PAS-00001.</summary>
    [Required, StringLength(10)] public string Code { get; set; } = "BB";
    public bool IsActive { get; set; } = true;

    public string DisplayName => City?.State is null ? Name : $"{Name}, {City.State.Name} {ZipCode}".Trim();
}
