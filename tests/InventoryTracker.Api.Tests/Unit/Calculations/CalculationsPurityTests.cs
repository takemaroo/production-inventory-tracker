namespace InventoryTracker.Api.Tests.Unit.Calculations;

/// <summary>
/// T-004 criterion "No EF Core, HTTP or DateTime.Now in Domain/Calculations" (architecture 3.3, 5.1): the
/// calculations must stay pure so they give the same answer every time for the same inputs.
/// </summary>
public class CalculationsPurityTests
{
    /// <summary>Text that would mean a calculation depends on the database, the web stack or the clock.</summary>
    private static readonly string[] ForbiddenText =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "System.Net.Http",
        "HttpContext",
        "DbContext",
        "DateTime.Now",
        "DateTime.UtcNow",
        "DateTimeOffset.Now",
        "DateTimeOffset.UtcNow",
        "TimeProvider",
        "IOptions",
    ];

    /// <summary>T-004: every source file in Domain/Calculations is free of database, HTTP and clock code.</summary>
    [Fact]
    public void Calculation_sources_use_no_database_http_or_clock()
    {
        var folder = Path.Combine(FindRepositoryRoot(), "src", "InventoryTracker.Api", "Domain", "Calculations");
        var files = Directory.GetFiles(folder, "*.cs");
        Assert.NotEmpty(files);

        var offences = files
            .SelectMany(file => ForbiddenText
                .Where(text => File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
                .Select(text => $"{Path.GetFileName(file)} contains \"{text}\""))
            .ToList();

        Assert.Empty(offences);
    }

    /// <summary>
    /// Walks up from the test binaries (tests/.../bin/Debug/net10.0) to the folder holding the solution file,
    /// so the test finds the source files on any machine.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InventoryTracker.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("InventoryTracker.slnx not found above " + AppContext.BaseDirectory);
    }
}
