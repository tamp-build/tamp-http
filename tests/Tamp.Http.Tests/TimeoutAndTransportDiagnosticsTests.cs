using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Tamp.Http.Tests;

/// <summary>
/// TAM-294 (strata-home's nightly post-mortem). A hung request used to surface only as
/// "the configured HttpClient.Timeout of 100 seconds elapsing". That message gives the limit, not the
/// time spent, and hides the real socket error several inner exceptions down. These tests pin the
/// replacement behaviour:
/// <list type="bullet">
///   <item>a client-enforced <see cref="TampApiClient.RequestTimeout"/></item>
///   <item>messages that state the elapsed time, the endpoint and the innermost cause</item>
///   <item>exception types that existing <c>catch (TaskCanceledException)</c> and
///     <c>catch (HttpRequestException)</c> retry logic still matches</item>
/// </list>
/// </summary>
public sealed class TimeoutAndTransportDiagnosticsTests
{
    private static readonly Uri Base = new("https://sink.example.com");

    /// <summary>Never responds. Waits until the request's token is cancelled, like a dead TCP peer.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Fails below HTTP the way SocketsHttpHandler does on a connect timeout.</summary>
    private sealed class SocketFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(
                HttpRequestError.ConnectionError,
                "Connection timed out (sink.example.com:443)",
                new SocketException((int)SocketError.TimedOut));
    }

    private static TestApiClient Client(HttpMessageHandler handler, TimeSpan? httpClientTimeout = null)
    {
        var http = new HttpClient(handler);
        if (httpClientTimeout is { } t) http.Timeout = t;
        return new TestApiClient(Base, ApiCredential.None, http);
    }

    // ---- RequestTimeout ----

    [Fact]
    public async Task RequestTimeout_Hang_Throws_ApiTimeoutException_With_Elapsed_Endpoint_And_Hang_Hint()
    {
        using var client = Client(new HangingHandler(), httpClientTimeout: Timeout.InfiniteTimeSpan);
        client.RequestTimeout = TimeSpan.FromMilliseconds(200);

        var ex = await Assert.ThrowsAsync<ApiTimeoutException>(() => client.Post("ingest/coverage", new { a = 1 }));

        Assert.False(ex.IsHttpClientTimeout);
        Assert.Equal("POST", ex.RequestMethod);
        Assert.Equal("https://sink.example.com/ingest/coverage", ex.RequestUri);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ex.Timeout);
        Assert.True(ex.Elapsed >= TimeSpan.FromMilliseconds(150), $"elapsed {ex.Elapsed}");

        Assert.Contains("POST https://sink.example.com/ingest/coverage timed out after ", ex.Message);
        Assert.Contains("RequestTimeout 0.2 s", ex.Message);
        Assert.Contains("a hang, not an error", ex.Message);
    }

    [Fact]
    public async Task Timeout_Keeps_The_HttpClient_Exception_Shape_For_Existing_Catch_Blocks()
    {
        using var client = Client(new HangingHandler(), httpClientTimeout: Timeout.InfiniteTimeSpan);
        client.RequestTimeout = TimeSpan.FromMilliseconds(100);

        // findings-ingest, findings-gate and the GitHub check publisher all match on TaskCanceledException.
        var ex = await Assert.ThrowsAnyAsync<TaskCanceledException>(() => client.Get<SamplePayload>("x"));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Injected_HttpClient_Timeout_Firing_First_Is_Named_As_The_Cause()
    {
        // strata-home's exact shape: `new TampIngestClient(uri, token, new HttpClient())`, so the
        // HttpClient's own (here shortened) Timeout beats a longer RequestTimeout.
        using var client = Client(new HangingHandler(), httpClientTimeout: TimeSpan.FromMilliseconds(200));
        client.RequestTimeout = TimeSpan.FromMinutes(5);

        var ex = await Assert.ThrowsAsync<ApiTimeoutException>(() => client.Post("ingest/sbom", new { a = 1 }));

        Assert.True(ex.IsHttpClientTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(200), ex.Timeout);
        Assert.Contains("injected HttpClient's own Timeout (0.2 s) fired before RequestTimeout (300.0 s)", ex.Message);
        Assert.Contains("Timeout.InfiniteTimeSpan", ex.Message);
    }

    [Fact]
    public async Task Caller_Cancellation_Stays_A_Plain_Cancellation_On_The_Callers_Token()
    {
        using var client = Client(new HangingHandler(), httpClientTimeout: Timeout.InfiniteTimeSpan);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetWithToken<SamplePayload>("x", cts.Token));

        Assert.IsNotType<ApiTimeoutException>(ex);
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Contains("GET https://sink.example.com/x was cancelled by the caller after ", ex.Message);
    }

    [Fact]
    public void Owned_HttpClient_Has_Its_Own_Timeout_Disabled_So_RequestTimeout_Governs()
    {
        using var client = new TestApiClient(Base, ApiCredential.None);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.HttpForTest().Timeout);
        Assert.Equal(TampApiClient.DefaultRequestTimeout, client.RequestTimeout);
    }

    [Fact]
    public void Injected_HttpClient_Timeout_Is_Left_Alone()
    {
        var http = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromSeconds(42) };
        using var client = new TestApiClient(Base, ApiCredential.None, http);
        Assert.Equal(TimeSpan.FromSeconds(42), http.Timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RequestTimeout_Rejects_Non_Positive_Values(int ms)
    {
        using var client = new TestApiClient(Base, ApiCredential.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.RequestTimeout = TimeSpan.FromMilliseconds(ms));
    }

    [Fact]
    public void RequestTimeout_Accepts_Infinite()
    {
        using var client = new TestApiClient(Base, ApiCredential.None);
        client.RequestTimeout = Timeout.InfiniteTimeSpan;
        Assert.Equal(Timeout.InfiniteTimeSpan, client.RequestTimeout);
    }

    [Fact]
    public void RequestTimeout_Rejects_Values_Above_CancelAfter_Range()
    {
        using var client = new TestApiClient(Base, ApiCredential.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.RequestTimeout = TimeSpan.FromDays(30));
    }

    // ---- transport failures ----

    [Fact]
    public async Task Transport_Failure_Names_Elapsed_Endpoint_And_Innermost_Socket_Cause()
    {
        using var client = Client(new SocketFailureHandler());

        var ex = await Assert.ThrowsAsync<ApiTransportException>(() => client.Post("ingest/coverage", new { a = 1 }));

        Assert.Equal("POST", ex.RequestMethod);
        Assert.Equal("https://sink.example.com/ingest/coverage", ex.RequestUri);
        Assert.Contains("POST https://sink.example.com/ingest/coverage failed after ", ex.Message);
        Assert.Contains("before any response arrived", ex.Message);
        Assert.Contains("Innermost cause: SocketException: ", ex.Message);
        Assert.Contains("(SocketError.TimedOut)", ex.Message);
    }

    [Fact]
    public async Task Transport_Failure_Stays_An_HttpRequestException_With_Its_Error_Kind()
    {
        using var client = Client(new SocketFailureHandler());

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.Get<SamplePayload>("x"));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        var original = Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.IsType<SocketException>(original.InnerException);
    }

    [Fact]
    public async Task SendRaw_Gets_The_Same_Transport_Diagnostics()
    {
        using var client = Client(new SocketFailureHandler());
        using var req = new HttpRequestMessage(HttpMethod.Post, "ingest/sbom-snapshots/x/provenance");

        var ex = await Assert.ThrowsAsync<ApiTransportException>(() => client.Raw(req));
        Assert.Equal("https://sink.example.com/ingest/sbom-snapshots/x/provenance", ex.RequestUri);
    }

    [Fact]
    public async Task SendRaw_Is_Bounded_By_RequestTimeout()
    {
        using var client = Client(new HangingHandler(), httpClientTimeout: Timeout.InfiniteTimeSpan);
        client.RequestTimeout = TimeSpan.FromMilliseconds(100);
        using var req = new HttpRequestMessage(HttpMethod.Get, "raw");

        await Assert.ThrowsAsync<ApiTimeoutException>(() => client.Raw(req));
    }

    // ---- HTTP error responses ----

    [Fact]
    public async Task Http_Error_Response_Records_Elapsed()
    {
        var handler = new RecordingHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) };
        using var client = new TestApiClient(Base, ApiCredential.None, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ApiServerException>(() => client.Get<SamplePayload>("x"));

        Assert.NotNull(ex.Elapsed);
        Assert.Matches(@"-> 502 Bad Gateway \(after \d+\.\d s\)$", ex.Message);
    }

    // ---- innermost-cause walk ----

    [Fact]
    public void InnermostCause_Walks_To_The_Bottom_Of_The_Chain()
    {
        var chain = new TaskCanceledException("outer",
            new TimeoutException("middle", new IOException("bottom", new SocketException((int)SocketError.ConnectionRefused))));
        var cause = ApiFailureText.InnermostCause(chain);
        Assert.StartsWith("SocketException: ", cause);
        Assert.EndsWith("(SocketError.ConnectionRefused)", cause);
    }

    [Fact]
    public void InnermostCause_Is_Null_When_There_Is_No_Inner_Exception()
        => Assert.Null(ApiFailureText.InnermostCause(new TaskCanceledException("alone")));

    [Fact]
    public void InnermostCause_Skips_Pure_Cancellation_Wrappers()
    {
        // HttpClient's own timeout shape: TCE -> TimeoutException -> TCE("A task was canceled."). No real cause.
        var chain = new TaskCanceledException("outer",
            new TimeoutException("timeout", new TaskCanceledException("A task was canceled.")));
        Assert.Null(ApiFailureText.InnermostCause(chain));
    }

    [Fact]
    public void InnermostCause_Finds_A_Socket_Error_Under_Cancellation_Wrappers()
    {
        // strata-home's shape: the socket error sat beneath the timeout and cancellation wrappers.
        var chain = new TaskCanceledException("outer",
            new TimeoutException("timeout",
                new HttpRequestException("Connection timed out", new SocketException((int)SocketError.TimedOut))));
        Assert.EndsWith("(SocketError.TimedOut)", ApiFailureText.InnermostCause(chain));
    }

    [Fact]
    public void Seconds_Is_Culture_Invariant()
    {
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("102.1 s", ApiFailureText.Seconds(TimeSpan.FromMilliseconds(102_100)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }
}
