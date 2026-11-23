// Program.cs is the composition root: it wires the application together and starts it.
// Business logic never lives here; it belongs in Features/. See docs/03-architecture.md section 2.
using System.Text.Json;
using System.Text.Json.Serialization;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Configuration;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.OpenApi;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

// SEC-01: the largest request body the server accepts, 1 MB (RequestLimits.MaxBodyBytes explains the size). Kestrel is
// the web server built into ASP.NET Core; its own default is 30 MB. A larger body is refused when the app starts reading
// it, with 413 request-too-large (ApiExceptionHandler, SEC-02), so a huge body never reaches the services. The IIS line
// applies the same limit if the release phase hosts the API inside IIS, where Kestrel's setting does not apply; when the
// API does not run in IIS it does nothing.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = RequestLimits.MaxBodyBytes);
builder.Services.Configure<IISServerOptions>(iis => iis.MaxRequestBodySize = RequestLimits.MaxBodyBytes);

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

// Design section 6: every error body is an RFC 9457 ProblemDetails object with a stable "code", a fixed
// title per status and a traceId. Also the central exception handler and the 400 for unreadable bodies.
builder.Services.AddApiErrorHandling();

// Architecture 6, Time: one clock for the whole app, reading the real system time. TryAdd adds it only if
// nothing has registered a TimeProvider yet, so it never duplicates one a framework library registered first.
// (Tests swap in a fake clock differently: the test factory registers its own TimeProvider after this file
// has run, and when a service is registered twice the last registration is the one that gets used.)
// Singleton means one shared instance for the app's lifetime, which is fine because a clock holds no
// per-request state.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Clock>();

// GET /health answers "Healthy" when the app is running and can reach its database.
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

// PRD section 5: publish an OpenAPI description of every endpoint (served at /openapi/v1.json in Development),
// with the bearer token scheme and the error responses (see Common/OpenApi/OpenApiSetup).
builder.Services.AddApiOpenApi();

// Every Features/**/*Service class, registered by convention (see FeatureRegistration).
builder.Services.AddFeatureServices();

// Who is calling, and may they do this? ASP.NET Core Identity (users, salted password hashes, roles, lock-out),
// JWT bearer tokens signed with JWT_SIGNING_KEY, and authorization where every endpoint needs a signed-in user
// unless it says [AllowAnonymous] (architecture 6, Authentication and Authorization). See AuthSetup.
builder.Services.AddApiAuthentication(builder.Configuration);

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// 2. Middleware pipeline.
// Middleware are steps every HTTP request passes through, in the order they are added here, and the
// response passes back through them in reverse. Order matters: the exception handler comes first so it
// can catch errors thrown by every later step.
// ---------------------------------------------------------------------------------------------

// Catches any exception thrown by a later step and hands it to ApiExceptionHandler, which writes the
// ProblemDetails body: the ApiException's own status and code, or 500 without stack traces (design section 6).
app.UseExceptionHandler();

// Gives bodiless error responses (404 unknown route, 405 wrong method) a ProblemDetails body too, with the
// codes not-found and method-not-allowed (architecture 4.1 item 8). The handler is passed explicitly because
// the plain UseStatusCodePages() answers in text/plain ("Status Code: 404; Not Found") when the client's
// Accept header does not mention JSON; ProblemResponse always answers in JSON (design 6).
app.UseStatusCodePages(context => ProblemResponse.WriteAsync(
    context.HttpContext, new ProblemDetails { Status = context.HttpContext.Response.StatusCode }));

// HTTPS redirection only outside Development and tests: locally the API runs on plain http://localhost.
if (app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

// Architecture 4.1 item 7: every 401 response carries the header WWW-Authenticate: Bearer.
app.UseBearerChallengeHeader();

// Authentication middleware: reads the bearer token, checks it, and sets HttpContext.User (or leaves the request
// anonymous). Authorization middleware: compares HttpContext.User with the endpoint's rules and answers 401 or 403
// before the controller runs (design 2.2: 401 first, then 403). Both come after error handling, so their responses
// get ProblemDetails bodies, and before the endpoints, so every endpoint below is protected.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Decisions #28 and #33: the health check and the Development-only OpenAPI document are infrastructure endpoints
// that reveal nothing, so they are the two exceptions to "every endpoint needs a token" (AC-01.5).
app.MapHealthChecks("/health").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// ---------------------------------------------------------------------------------------------
// 3. Start-up work, then run.
// ---------------------------------------------------------------------------------------------

// Architecture 6, "First Manager and start-up checks", in this order. A failed check stops the app before it
// listens, with the design 7.4 message in the console and exit code 1 (US-02, PRD section 5).
try
{
    // (1) tolerance 0-100 and (2) JWT_SIGNING_KEY: settings only, so before the database is touched.
    StartupChecks.CheckSettings(app.Configuration);

    // (3) Creates the SQLite file on first run and applies any new migrations.
    await app.Services.MigrateDatabaseAsync();

    // (4) the four roles of Roles.All and (5) the first Manager from ADMIN_EMAIL / ADMIN_PASSWORD, when there are no users.
    await StartupChecks.SeedRolesAndFirstManagerAsync(app.Services, app.Configuration);
}
catch (StartupException exception)
{
    app.Logger.LogCritical("{Message}", exception.Message);

    // Disposing the app flushes the console logger, which writes on a background thread; without it the message
    // could be lost when the process ends.
    await app.DisposeAsync();
    return 1;
}

await app.RunAsync();
return 0;

/// <summary>
/// The application's entry point. Declared partial and public so the integration tests can start the
/// real application in memory with <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
