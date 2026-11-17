using BushiBan.Web.Features.Crm;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    static partial void RegisterCrm(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<PeopleService>();
        services.AddScoped<DashboardService>();
    }
}
