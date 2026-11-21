namespace InventoryTracker.Api.Tests.Acceptance;

/// <summary>
/// QA acceptance tests (phase 5) for AC-02.5 and the PRD section 5 security rule that secrets come from environment
/// variables, never from committed files. No earlier test checked <c>.env.example</c> or the committed settings files.
/// </summary>
/// <remarks>
/// These tests read files in the repository, not the running app: AC-02.5 is about what is committed. The repository
/// root is found by walking up from the test binaries to the folder that holds <c>InventoryTracker.slnx</c>, the same
/// way <c>OpenApiTests</c> finds the <c>.http</c> file, so the tests work from any build output folder.
/// </remarks>
public class SecretsConfigurationAcceptanceTests
{
    /// <summary>
    /// The secrets the application needs, typed out from the PRD (US-02, section 5) and design 7.4, not read from the
    /// code under test, so a renamed or newly required secret makes this list and the code disagree.
    /// </summary>
    public static TheoryData<string> Secrets => new() { "ADMIN_EMAIL", "ADMIN_PASSWORD", "JWT_SIGNING_KEY" };

    /// <summary>
    /// AC-02.5: <c>.env.example</c> lists <c>ADMIN_EMAIL</c>, <c>ADMIN_PASSWORD</c> and the token signing key, each as a
    /// <c>NAME=</c> line whose value is empty (a placeholder), so the file can never carry a real value by accident.
    /// </summary>
    [Theory]
    [MemberData(nameof(Secrets))]
    public void AC_02_5_env_example_lists_every_secret_with_a_placeholder_value(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), ".env.example"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        var entry = Assert.Single(lines, line => line.StartsWith(name + "=", StringComparison.Ordinal));
        // Assert.True with a fixed message, not Assert.Equal: a failure must never print the value it found.
        Assert.True(entry == name + "=", $"{name} has a value in .env.example; it must be an empty placeholder.");
    }

    /// <summary>
    /// AC-02.5 and PRD section 5: no committed settings file of the app (<c>appsettings*.json</c>,
    /// <c>Properties/launchSettings.json</c>) names a secret at all, so none can hold a value for it. The app reads them
    /// only from environment variables or, in Development, <c>dotnet user-secrets</c> outside the repository.
    /// </summary>
    [Theory]
    [MemberData(nameof(Secrets))]
    public void AC_02_5_committed_settings_files_hold_no_secret(string name)
    {
        var app = Path.Combine(RepositoryRoot(), "src", "InventoryTracker.Api");
        var files = Directory.GetFiles(app, "appsettings*.json")
            .Append(Path.Combine(app, "Properties", "launchSettings.json"))
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            // Assert.False with a fixed message, not Assert.DoesNotContain: that would print the file's text, which
            // could include a secret value, on failure.
            Assert.False(File.ReadAllText(file).Contains(name, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} names {name}; secrets must come from environment variables.");
        }
    }

    /// <summary>
    /// AC-02.5: the sample requests read the first Manager's email and password with <c>{{$dotenv ...}}</c> from a
    /// git-ignored <c>.env</c>, and never assign them in the tracked <c>.http</c> file itself. The only lines that may
    /// show <c>NAME=value</c> are comments, which carry the architecture's documented sample values.
    /// </summary>
    [Fact]
    public void AC_02_5_sample_requests_read_the_admin_credentials_from_a_git_ignored_file()
    {
        var root = RepositoryRoot();
        var http = File.ReadAllLines(Path.Combine(root, "src", "InventoryTracker.Api", "InventoryTracker.Api.http"));
        var code = http.Where(line => !line.TrimStart().StartsWith('#')).ToList();

        Assert.Contains(code, line => line.Contains("{{$dotenv ADMIN_EMAIL}}", StringComparison.Ordinal));
        Assert.Contains(code, line => line.Contains("{{$dotenv ADMIN_PASSWORD}}", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains("ADMIN_PASSWORD=", StringComparison.Ordinal)
            || line.Contains("JWT_SIGNING_KEY", StringComparison.Ordinal));

        // The .env the sample requests read must be ignored by git (a bare ".env" pattern matches it in any folder).
        var ignored = File.ReadAllLines(Path.Combine(root, ".gitignore")).Select(line => line.Trim());
        Assert.Contains(".env", ignored);
    }

    /// <summary>The folder holding <c>InventoryTracker.slnx</c>, found by walking up from the test binaries.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InventoryTracker.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("InventoryTracker.slnx not found above " + AppContext.BaseDirectory);
    }
}
