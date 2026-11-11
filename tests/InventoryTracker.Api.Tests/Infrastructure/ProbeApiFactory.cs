using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A <see cref="TestApiFactory"/> that also serves the test-only <see cref="ErrorProbeController"/> and
/// captures the log, for tests of the shared error handling (T-001).
/// </summary>
/// <remarks>
/// <c>ConfigureServices</c> here runs after Program.cs has registered its own services, so it can add to them.
/// <c>AddApplicationPart</c> tells MVC to look for controllers in the test assembly as well as in the API.
/// </remarks>
public class ProbeApiFactory : TestApiFactory
{
    /// <summary>Every log entry the app wrote while this factory was running.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Adds the probe controller and the capturing logger on top of the normal test set-up.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ErrorProbeController).Assembly);
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }
}
