// Program.cs is the composition root: it wires the application together and starts it.
// Business logic never lives here; it belongs in Features/. See docs/03-architecture.md section 2.
using System.Text.Json;
using System.Text.Json.Serialization;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Features;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// 1. Services (dependency injection registrations).
// Each Add... call tells the DI container how to build a service. A class that needs the service
// lists it as a constructor parameter and receives it automatically.
// ---------------------------------------------------------------------------------------------

// D-1: bind the "Inventory" section of appsettings.json (tolerance) to a typed options class.
builder.Services.Configure<InventoryOptions>(builder.Configuration.GetSection(InventoryOptions.SectionName));

// EF Core with SQLite. Migrations are applied further down, before the app starts listening.
builder.Services.AddInventoryDatabase(builder.Configuration, builder.Environment);

// Controllers are classes whose public methods handle HTTP requests (one controller per resource).
builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Design 2.3: enums travel as their names ("Created", not 0); requests accept any letter case.
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    // Design 2.4: a quantity sent as a string ("12.5") is rejected. ASP.NET Core's web defaults would accept it.
    options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    // Design 2.3: every response field is always present; unknown values are null rather than omitted.
    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// Design section 6: every error body is an RFC 9457 ProblemDetails object.
// T-001 adds the stable "code" field, fixed titles and the exception-to-status mapping.
builder.Services.AddProblemDetails();

// GET /health answers "Healthy" when the app is running and can reach its database.
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

// PRD section 5: publish an OpenAPI description of every endpoint (served at /openapi/v1.json in Development).
builder.Services.AddOpenApi();

// Every Features/**/*Service class, registered by convention (see FeatureRegistration).
builder.Services.AddFeatureServices();

// T-003 adds here: ASP.NET Core Identity, JWT bearer authentication, authorization with a fallback policy
// that requires a signed-in user, and the start-up checks (JWT_SIGNING_KEY, tolerance range).

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// 2. Middleware pipeline.
// Middleware are steps every HTTP request passes through, in the order they are added here, and the
// response passes back through them in reverse. Order matters: the exception handler comes first so it
// can catch errors thrown by every later step.
// ---------------------------------------------------------------------------------------------

// Turns an unhandled exception into a 500 ProblemDetails body, without stack traces (design section 6).
app.UseExceptionHandler();

// Gives bodiless error responses (404 unknown route, 405 wrong method) a ProblemDetails body too.
app.UseStatusCodePages();

// HTTPS redirection only outside Development and tests: locally the API runs on plain http://localhost.
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

// T-003 adds app.UseAuthentication() and app.UseAuthorization() here: after error handling,
// before the endpoints, so every endpoint below is protected.

app.MapControllers();
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ---------------------------------------------------------------------------------------------
// 3. Start-up work, then run.
// ---------------------------------------------------------------------------------------------

// Creates the SQLite file on first run and applies any new migrations.
await app.Services.MigrateDatabaseAsync();

// T-003 adds the role seeding and the first Manager from ADMIN_EMAIL / ADMIN_PASSWORD here (US-02).

await app.RunAsync();

/// <summary>
/// The application's entry point. Declared partial and public so the integration tests can start the
/// real application in memory with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
