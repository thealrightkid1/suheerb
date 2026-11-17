namespace BushiBan.Web.Infrastructure;

/// <summary>
/// Each feature module registers its services in its own file by implementing one of these partial methods,
/// e.g. Features/Billing/BillingModule.cs implements RegisterBilling. Unimplemented ones are simply skipped.
/// </summary>
public static partial class ModuleRegistration
{
    public static IServiceCollection AddAppModules(this IServiceCollection services, IConfiguration config)
    {
        RegisterCrm(services, config);
        RegisterAttendance(services, config);
        RegisterRegistration(services, config);
        RegisterBilling(services, config);
        RegisterAccounting(services, config);
        RegisterCommunications(services, config);
        RegisterAdministration(services, config);
        return services;
    }

    static partial void RegisterCrm(IServiceCollection services, IConfiguration config);
    static partial void RegisterAttendance(IServiceCollection services, IConfiguration config);
    static partial void RegisterRegistration(IServiceCollection services, IConfiguration config);
    static partial void RegisterBilling(IServiceCollection services, IConfiguration config);
    static partial void RegisterAccounting(IServiceCollection services, IConfiguration config);
    static partial void RegisterCommunications(IServiceCollection services, IConfiguration config);
    static partial void RegisterAdministration(IServiceCollection services, IConfiguration config);
}
