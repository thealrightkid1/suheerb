using BushiBan.Web.Features.Administration;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    static partial void RegisterAdministration(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<UserAdministration>();
    }
}
