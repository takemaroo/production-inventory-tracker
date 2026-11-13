using System.Reflection;

namespace InventoryTracker.Api.Features;

/// <summary>
/// Registers every feature service with dependency injection by naming convention.
/// </summary>
/// <remarks>
/// Dependency injection (DI): instead of a class creating the objects it needs with <c>new</c>, it lists them
/// as constructor parameters and ASP.NET Core supplies them. ASP.NET Core needs a registration for each type.
/// Writing one line per service in Program.cs would make every task edit that file, so tasks could not run
/// side by side. Instead, every concrete class in the <c>InventoryTracker.Api.Features</c> namespace whose name
/// ends in <c>Service</c> is registered here as "scoped": one instance per HTTP request, the same lifetime as
/// the DbContext it uses. The stock building blocks that services share, <c>StockLedger</c> and <c>StockQueries</c>
/// (names ending in <c>Ledger</c> or <c>Queries</c>), are registered the same way (decision #77).
/// </remarks>
public static class FeatureRegistration
{
    /// <summary>
    /// Registers all <c>*Service</c>, <c>*Ledger</c> and <c>*Queries</c> classes under
    /// <c>InventoryTracker.Api.Features</c> as scoped services.
    /// </summary>
    public static IServiceCollection AddFeatureServices(this IServiceCollection services)
    {
        var serviceTypes = Assembly.GetExecutingAssembly().GetTypes().Where(type =>
            type is { IsClass: true, IsAbstract: false, IsNested: false } &&
            type.Namespace?.StartsWith("InventoryTracker.Api.Features", StringComparison.Ordinal) == true &&
            (type.Name.EndsWith("Service", StringComparison.Ordinal)
             || type.Name.EndsWith("Ledger", StringComparison.Ordinal)
             || type.Name.EndsWith("Queries", StringComparison.Ordinal)));

        foreach (var type in serviceTypes)
        {
            services.AddScoped(type);
        }

        return services;
    }
}
