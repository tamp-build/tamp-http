using System.Net;
using Xunit;

namespace Tamp.Http.Tests;

/// <summary>
/// HttpProbe.WaitForHealthy — post-deploy smoke pattern (Tamp.Http 0.1.1+).
/// </summary>
public sealed class HttpProbeTests
{
    [Fact]
    public async Task Returns_Immediately_When_First_Response_Is_2xx()
    {
        var handler = new RecordingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        using var client = new HttpClient(handler);

        await HttpProbe.WaitForHealthy("https://example/health", TimeSpan.FromSeconds(5), httpClient: client);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Polls_Until_Healthy()
    {
        var attempts = 0;
        var handler = new RecordingHandler
        {
            Responder = _ =>
            {
                attempts++;
                return new HttpResponseMessage(attempts < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            }
        };
        using var client = new HttpClient(handler);

        await HttpProbe.WaitForHealthy(
            "https://example/health",
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(10),
            httpClient: client);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Throws_TimeoutException_When_Never_Healthy()
    {
        var handler = new RecordingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            HttpProbe.WaitForHealthy(
                "https://example/health",
                timeout: TimeSpan.FromMilliseconds(200),
                interval: TimeSpan.FromMilliseconds(50),
                httpClient: client));

        Assert.Contains("WaitForHealthy", ex.Message);
        Assert.Contains("500", ex.Message);
        Assert.Contains("attempt", ex.Message);
    }

    [Fact]
    public async Task Treats_HttpRequestException_As_Transient_And_Retries()
    {
        var attempts = 0;
        var handler = new RecordingHandler
        {
            Responder = _ =>
            {
                attempts++;
                if (attempts == 1) throw new HttpRequestException("Connection refused");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        using var client = new HttpClient(handler);

        await HttpProbe.WaitForHealthy(
            "https://example/health",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(10),
            httpClient: client);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Custom_IsHealthy_Inspects_Status_Beyond_2xx()
    {
        // Predicate that only accepts 200 OK exactly (not generic 2xx). 204 should keep polling.
        var attempts = 0;
        var handler = new RecordingHandler
        {
            Responder = _ =>
            {
                attempts++;
                return new HttpResponseMessage(attempts < 3 ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            }
        };
        using var client = new HttpClient(handler);

        await HttpProbe.WaitForHealthy(
            "https://example/health",
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(10),
            isHealthy: r => ValueTask.FromResult(r.StatusCode == HttpStatusCode.OK),
            httpClient: client);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Headers_Are_Sent_On_Each_Request()
    {
        var handler = new RecordingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        using var client = new HttpClient(handler);

        await HttpProbe.WaitForHealthy(
            "https://example/health",
            timeout: TimeSpan.FromSeconds(5),
            headers: new Dictionary<string, string> { ["X-Probe-Key"] = "abc123" },
            httpClient: client);

        Assert.Single(handler.Requests);
        Assert.True(handler.Requests[0].Headers.TryGetValue("X-Probe-Key", out var vals));
        Assert.Contains("abc123", vals!);
    }

    [Fact]
    public async Task Cancellation_Aborts_Immediately()
    {
        var handler = new RecordingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpProbe.WaitForHealthy(
                "https://example/health",
                timeout: TimeSpan.FromSeconds(30),  // long enough that cancellation, not timeout, fires
                interval: TimeSpan.FromMilliseconds(100),
                httpClient: client,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task Null_Or_Empty_Url_Throws_ArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HttpProbe.WaitForHealthy("", TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HttpProbe.WaitForHealthy("   ", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Zero_Or_Negative_Timeout_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HttpProbe.WaitForHealthy("https://x", TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HttpProbe.WaitForHealthy("https://x", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Zero_Or_Negative_Interval_Throws_When_Explicit()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HttpProbe.WaitForHealthy("https://x", TimeSpan.FromSeconds(1), interval: TimeSpan.Zero));
    }
}
