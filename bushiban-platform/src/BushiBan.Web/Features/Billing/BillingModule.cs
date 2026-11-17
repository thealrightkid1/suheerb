using BushiBan.Web.Features.Billing;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    static partial void RegisterBilling(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<BillingService>();
    }
}
