using BushiBan.Web.Features.Accounting;

namespace BushiBan.Web.Infrastructure;

public static partial class ModuleRegistration
{
    static partial void RegisterAccounting(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<AccountCodeService>();
        services.AddScoped<FinancialReportService>();
    }
}
