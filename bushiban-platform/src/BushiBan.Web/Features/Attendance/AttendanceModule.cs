using BushiBan.Web.Features.Academics;
using BushiBan.Web.Features.Attendance;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    /// <summary>Attendance (roll call, records, kiosk check-in) and academics (programs, ranking systems, promotions).</summary>
    static partial void RegisterAttendance(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<AttendanceService>();
        services.AddScoped<PromotionService>();
        services.AddSingleton<KioskPinThrottle>();
    }

    /// <summary>Registration kiosk and the form builder.</summary>
    static partial void RegisterRegistration(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<KioskFormService>();
    }
}
