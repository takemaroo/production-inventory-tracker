using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A <see cref="TestApiFactory"/> that also serves the test-only probe controllers (<see cref="ErrorProbeController"/>
/// for the shared error handling, T-001, and <see cref="AuthProbeController"/> for authorization, T-003).
/// </summary>
/// <remarks>
/// <c>ConfigureServices</c> here runs after Program.cs has registered its own services, so it can add to them.
/// <c>AddApplicationPart</c> tells MVC to look for controllers in the test assembly as well as in the API.
/// The log is captured by the base class (<see cref="TestApiFactory.Logs"/>).
/// </remarks>
public class ProbeApiFactory : TestApiFactory
{
    /// <summary>Adds the probe controllers on top of the normal test set-up.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ErrorProbeController).Assembly));
    }
}
