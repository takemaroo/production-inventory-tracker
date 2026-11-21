using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// SEC-01 and SEC-02 against the real web server: a body over 1 MB is refused with <c>413</c> <c>request-too-large</c>,
/// a broken body with <c>400</c>, both as design 6 bodies, and neither leaves anything at Warning or Error in the log.
/// </summary>
/// <remarks>
/// <para>
/// The other integration tests use the in-memory test server, which has no body size limit at all, so it cannot show
/// the limit set in Program.cs. <see cref="KestrelApiFactory"/> starts the same app on Kestrel, listening on a free port
/// of this machine, and these tests talk to it over a real TCP connection.
/// </para>
/// <para>
/// The requests are written byte by byte, as the security reviewer's probes did (<c>p6_cl.py</c>, <c>p7_biglines.py</c>,
/// <c>p15_logflood.py</c>), because an ordinary <see cref="HttpClient"/> cannot send a header that lies about the body's
/// length or a malformed chunk. Each request says <c>Connection: close</c>, so the server ends the connection after its
/// answer and the test can read to the end.
/// </para>
/// </remarks>
public class RequestBodyLimitTests(RequestBodyLimitTests.KestrelApiFactory app) : IClassFixture<RequestBodyLimitTests.KestrelApiFactory>
{
    private const string SignInPath = "/api/v1/auth/sign-in";

    // How many raw requests in the current test had to be sent a second time (see SendRawAsync). xUnit creates a new
    // instance of this class for every test, so this always starts at 0.
    private int _retries;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// SEC-02, the reviewer's <c>p6_cl.py</c>: an anonymous sign-in whose header claims 31,000,000 bytes, with only
    /// <c>{}</c> sent, gets <c>413</c> <c>request-too-large</c> "The request body is too large. The limit is 1 MB." with
    /// the fixed title and no <c>errors</c>, instead of <c>500</c>; the log gets one Information line, no exception.
    /// </summary>
    [Fact]
    public async Task Header_claiming_31_MB_gives_413_not_500()
    {
        var logStart = app.Logs.Entries.Count;

        var response = await SendRawAsync(Head("POST", SignInPath, "Content-Length: 31000000"), "{}"u8.ToArray());

        AssertTooLarge(response);
        AssertNoWarningsOrErrorsLoggedSince(logStart);
        Assert.Contains(EntriesSince(logStart), entry =>
            entry.Category == typeof(ApiExceptionHandler).FullName
            && entry.Level == LogLevel.Information
            && entry.Exception is null
            && entry.Message.Contains("refused with 413", StringComparison.Ordinal));
    }

    /// <summary>
    /// SEC-01, the limit's boundary on sign-in: a body of exactly 1,000,000 bytes reaches the service (<c>401</c>
    /// <c>invalid-credentials</c>, an unknown email), and one byte more, sent in full, gets <c>413</c>.
    /// </summary>
    [Fact]
    public async Task One_MB_is_accepted_and_one_byte_more_is_refused()
    {
        var logStart = app.Logs.Entries.Count;
        using var client = new HttpClient { BaseAddress = app.ServerAddress };
        var atLimit = SignInBody(RequestLimits.MaxBodyBytes);

        using var accepted = await client.PostAsync(SignInPath, new ByteArrayContent(atLimit)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        }, Cancel);

        Assert.Equal(1_000_000, atLimit.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, accepted.StatusCode);
        Assert.Equal("invalid-credentials", (await ApiJson.ReadAsync(accepted)).GetProperty("code").GetString());

        var overLimit = SignInBody(RequestLimits.MaxBodyBytes + 1);
        AssertTooLarge(await SendRawAsync(Head("POST", SignInPath, $"Content-Length: {overLimit.Length}"), overLimit));
        AssertNoWarningsOrErrorsLoggedSince(logStart);
    }

    /// <summary>
    /// SEC-01: a body with no declared length (<c>Transfer-Encoding: chunked</c>) is counted as it arrives, and gets
    /// <c>413</c> once it passes 1 MB.
    /// </summary>
    [Fact]
    public async Task Chunked_body_over_1_MB_gives_413()
    {
        var logStart = app.Logs.Entries.Count;
        var chunked = Chunked(SignInBody(RequestLimits.MaxBodyBytes + 50_000));

        var response = await SendRawAsync(Head("POST", SignInPath, "Transfer-Encoding: chunked"), chunked);

        AssertTooLarge(response);
        AssertNoWarningsOrErrorsLoggedSince(logStart);
    }

    /// <summary>
    /// SEC-02, the reviewer's malformed chunked body (<c>ZZ</c> where a chunk size belongs): <c>400</c> <c>validation</c>
    /// "The request body could not be read.", without <c>errors</c>, instead of <c>500</c>.
    /// </summary>
    [Fact]
    public async Task Malformed_chunked_body_gives_400_not_500()
    {
        var logStart = app.Logs.Entries.Count;

        var response = await SendRawAsync(Head("POST", SignInPath, "Transfer-Encoding: chunked"), "ZZ\r\n"u8.ToArray());

        Assert.Equal(400, response.Status);
        var body = JsonDocument.Parse(response.Body).RootElement;
        Assert.Equal("Invalid request", body.GetProperty("title").GetString());
        Assert.Equal("validation", body.GetProperty("code").GetString());
        Assert.Equal("The request body could not be read.", body.GetProperty("detail").GetString());
        Assert.False(body.TryGetProperty("errors", out _));
        AssertNoWarningsOrErrorsLoggedSince(logStart);
    }

    /// <summary>
    /// SEC-01 regression, the reviewer's largest <c>p7_biglines.py</c> body: a Warehouse user's send of 3,000,000
    /// <c>null</c> lines (15 MB) is refused with <c>413</c> as soon as its header arrives. Before the fix it was read,
    /// held the write lock for 9.1 seconds and answered with 412 MB.
    /// </summary>
    [Fact]
    public async Task Reviewers_15_MB_send_gives_413()
    {
        using var manager = await CreateClientAsync(Roles.Manager);
        var (number, _, _) = await TwoLineOrderAsync(manager);
        var logStart = app.Logs.Entries.Count;
        var token = await SignInAsync(Roles.Warehouse);
        const int nullLines = 3_000_000;
        var length = "{\"lines\":[".Length + (nullLines * 5) - 1 + "]}".Length;
        var start = Encoding.ASCII.GetBytes("{\"lines\":[" + string.Join(",", Enumerable.Repeat("null", 10_000)));

        var response = await SendRawAsync(
            Head("POST", $"{OrdersPath}/{number}/send", $"Content-Length: {length}", $"Authorization: Bearer {token}"), start);

        AssertTooLarge(response);
        AssertNoWarningsOrErrorsLoggedSince(logStart);
    }

    /// <summary>
    /// SEC-02 regression, the reviewer's <c>p15_logflood.py</c> at test size: 56 anonymous header-only requests, 7 from
    /// each of 8 parallel clients, each get <c>413</c>, and the log gains one short Information line per request and nothing at
    /// Warning or above. Before the fix every one was an Error with a stack trace of about 27 lines (2.6 MB a second).
    /// </summary>
    [Fact]
    public async Task Flood_of_oversized_headers_leaves_short_information_lines_only()
    {
        var logStart = app.Logs.Entries.Count;
        var head = Head("POST", SignInPath, "Content-Length: 31000000");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var statuses = new List<int>();
            for (var request = 0; request < 7; request++)
            {
                statuses.Add((await SendRawAsync(head, body: null)).Status);
            }

            return statuses;
        }));

        var statuses = responses.SelectMany(list => list).ToList();
        Assert.All(statuses, status => Assert.Equal(413, status));
        var added = EntriesSince(logStart).Where(entry => entry.Category == typeof(ApiExceptionHandler).FullName).ToList();

        // One line per request. A request sent a second time (SendRawAsync) may have been logged on both tries.
        Assert.InRange(added.Count, statuses.Count, statuses.Count + _retries);
        Assert.All(added, entry =>
        {
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
            Assert.True(entry.Message.Length < 300, entry.Message);
        });
        AssertNoWarningsOrErrorsLoggedSince(logStart);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The log entries added after the first <paramref name="logStart"/> (a count taken at the start of a test), so a
    /// test looks only at what its own requests caused, not at what the class's earlier tests left in the shared log.
    /// </summary>
    private List<CapturingLoggerProvider.LogEntry> EntriesSince(int logStart) => app.Logs.Entries.Skip(logStart).ToList();

    /// <summary>
    /// SEC-02: since <paramref name="logStart"/>, nothing in the log at Warning or above, and no entry carrying the
    /// exception (and so the stack trace) of a refused body.
    /// </summary>
    private void AssertNoWarningsOrErrorsLoggedSince(int logStart)
    {
        var entries = EntriesSince(logStart);
        Assert.DoesNotContain(entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(entries, entry => entry.Exception is Microsoft.AspNetCore.Http.BadHttpRequestException);
    }

    private static void AssertTooLarge(RawResponse response)
    {
        Assert.Equal(413, response.Status);
        Assert.Contains("content-type: application/problem+json", response.Headers, StringComparison.OrdinalIgnoreCase);
        var body = JsonDocument.Parse(response.Body).RootElement;
        Assert.Equal(413, body.GetProperty("status").GetInt32());
        Assert.Equal("Request too large", body.GetProperty("title").GetString());
        Assert.Equal("request-too-large", body.GetProperty("code").GetString());
        Assert.Equal("The request body is too large. The limit is 1 MB.", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.False(body.TryGetProperty("errors", out _));
    }

    /// <summary>A sign-in body of exactly <paramref name="length"/> bytes: an unknown email and a long password.</summary>
    private static byte[] SignInBody(long length)
    {
        const string prefix = "{\"email\":\"nobody@inventory.test\",\"password\":\"";
        const string suffix = "\"}";
        return Encoding.ASCII.GetBytes(prefix + new string('A', (int)length - prefix.Length - suffix.Length) + suffix);
    }

    /// <summary>The request line and headers, ending with the blank line, for a JSON request that closes the connection.</summary>
    private string Head(string method, string path, params string[] headers) =>
        $"{method} {path} HTTP/1.1\r\nHost: {app.ServerAddress.Authority}\r\nContent-Type: application/json\r\n" +
        $"Connection: close\r\n{string.Join("", headers.Select(header => header + "\r\n"))}\r\n";

    /// <summary><paramref name="body"/> in HTTP/1.1 chunked framing: 64 KB chunks, then the empty last chunk.</summary>
    private static byte[] Chunked(byte[] body)
    {
        var framed = new MemoryStream();
        foreach (var chunk in body.Chunk(65_536))
        {
            framed.Write(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"));
            framed.Write(chunk);
            framed.Write("\r\n"u8);
        }

        framed.Write("0\r\n\r\n"u8);
        return framed.ToArray();
    }

    /// <summary>
    /// Sends <paramref name="head"/> and then <paramref name="body"/> over a new TCP connection and reads the answer until
    /// the server closes the connection. The body is written alongside the reading, and a failure to write it is
    /// ignored: the server may answer and close before it has read everything, which is the point of a size limit.
    /// </summary>
    /// <remarks>
    /// When the server refuses a body and closes the connection while the client is still sending, Windows may reset the
    /// connection and throw away the answer before the test reads it. That says nothing about the API, so the request is
    /// sent once more. If it is reset again before any answer arrives, the test fails with a message saying so.
    /// </remarks>
    private async Task<RawResponse> SendRawAsync(string head, byte[]? body)
    {
        var received = await TrySendRawAsync(head, body);
        if (received is null)
        {
            Interlocked.Increment(ref _retries);
            received = await TrySendRawAsync(head, body);
        }

        if (received is null)
        {
            Assert.Fail("Connection reset before any answer was read");
        }

        return RawResponse.Parse(received);
    }

    /// <summary>One try of <see cref="SendRawAsync"/>: the bytes received, or null when the connection was reset first.</summary>
    private async Task<byte[]?> TrySendRawAsync(string head, byte[]? body)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(app.ServerAddress.Host, app.ServerAddress.Port, Cancel);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), Cancel);
        var writing = body is null ? Task.CompletedTask : WriteIgnoringClosedAsync(stream, body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var received = new MemoryStream();
        var buffer = new byte[65_536];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, timeout.Token);
            }
            catch (IOException)
            {
                // The server reset the connection. After part of an answer, what was received is the whole answer;
                // before any of it, the answer was lost and the caller tries again.
                await writing;
                return received.Length > 0 ? received.ToArray() : null;
            }

            if (read == 0)
            {
                break;
            }

            received.Write(buffer, 0, read);
        }

        await writing;
        return received.ToArray();
    }

    private static async Task WriteIgnoringClosedAsync(Stream stream, byte[] body)
    {
        try
        {
            await stream.WriteAsync(body, Cancel);
        }
        catch (IOException)
        {
            // The server stopped reading and closed the connection: expected for a refused body.
        }
    }

    private async Task<HttpClient> CreateClientAsync(string role)
    {
        var client = new HttpClient { BaseAddress = app.ServerAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync(role));
        return client;
    }

    /// <summary>Creates a user with <paramref name="role"/> and signs in over the real server, returning the token.</summary>
    private async Task<string> SignInAsync(string role)
    {
        var email = Seed.UniqueEmail("limits");
        await app.CreateUserAsync(email, role);
        using var client = new HttpClient { BaseAddress = app.ServerAddress };
        using var response = await client.PostAsync(SignInPath,
            ApiJson.Content(JsonSerializer.Serialize(new { email, password = TestApiFactory.UserPassword })), Cancel);
        response.EnsureSuccessStatusCode();
        return (await ApiJson.ReadAsync(response)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>
    /// The test app on Kestrel, the real web server, on a free port of this machine (port 0 lets the system choose),
    /// rather than the in-memory test server. Everything else is <see cref="TestApiFactory"/>: its own database, the
    /// test secrets and the captured log.
    /// </summary>
    public sealed class KestrelApiFactory : TestApiFactory
    {
        private Uri? _serverAddress;

        /// <summary>Asks <c>WebApplicationFactory</c> for Kestrel; it must be chosen before the app starts.</summary>
        public KestrelApiFactory() => UseKestrel(0);

        /// <summary>The address Kestrel listens on, for example <c>http://127.0.0.1:53124</c>. Starts the app if needed.</summary>
        public Uri ServerAddress => _serverAddress ??= FindServerAddress();

        private Uri FindServerAddress()
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            return new Uri(addresses.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                .Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
        }
    }

    /// <summary>An HTTP answer read off the socket: the status, the header block, and the body (de-chunked if needed).</summary>
    private sealed record RawResponse(int Status, string Headers, string Body)
    {
        public static RawResponse Parse(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.True(end > 0, $"No complete HTTP answer was received: '{text}'");
            var headers = text[..end];
            var status = int.Parse(headers.Split(' ', 3)[1], System.Globalization.CultureInfo.InvariantCulture);
            var body = text[(end + 4)..];
            if (headers.Contains("transfer-encoding: chunked", StringComparison.OrdinalIgnoreCase))
            {
                body = Unchunk(body);
            }

            return new RawResponse(status, headers, body);
        }

        private static string Unchunk(string chunked)
        {
            var body = new StringBuilder();
            var position = 0;
            while (true)
            {
                var lineEnd = chunked.IndexOf("\r\n", position, StringComparison.Ordinal);
                var size = Convert.ToInt32(chunked[position..lineEnd].Split(';')[0], 16);
                if (size == 0)
                {
                    return body.ToString();
                }

                body.Append(chunked, lineEnd + 2, size);
                position = lineEnd + 2 + size + 2;
            }
        }
    }
}
