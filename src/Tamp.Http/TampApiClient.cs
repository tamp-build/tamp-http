using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamp.Http;

/// <summary>
/// Base class for typed HTTP-API wrappers in Tamp. Subclasses expose
/// domain-shaped endpoint methods (PullRequests, Issues, ...) and
/// call <see cref="GetAsync{T}"/> / <see cref="PostJsonAsync{T}"/> /
/// etc. for the heavy lifting.
///
/// Auth credentials carrying a <see cref="Secret"/> automatically
/// join the runner's redaction table so any logged value is scrubbed.
///
/// Response bodies are deserialized via <see cref="JsonSerializer"/>
/// with case-insensitive property name matching (the dialect used by
/// every major REST API).
///
/// Error responses (4xx / 5xx) throw <see cref="ApiException"/>
/// — <see cref="ApiClientException"/> for 4xx, <see cref="ApiServerException"/>
/// for 5xx. Body is captured for diagnostics, truncated if huge.
/// </summary>
public abstract class TampApiClient : IDisposable
{
    /// <summary>Base URL for all requests. Relative paths in endpoint methods are combined against this.</summary>
    public Uri BaseUri { get; }

    /// <summary>Underlying HttpClient. Exposed for subclasses that need to tune timeouts, headers, etc.</summary>
    protected HttpClient Http { get; }

    /// <summary>Authentication credential applied to every outgoing request.</summary>
    protected ApiCredential Credential { get; }

    /// <summary>JSON options used for both serialization and deserialization. Subclasses can override to add converters.</summary>
    protected virtual JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Max response body size (in bytes) to capture into <see cref="ApiException.ResponseBody"/>. Default: 16 KiB. Larger bodies are truncated.</summary>
    public int MaxCapturedErrorBodyBytes { get; set; } = 16 * 1024;

    /// <summary>If <c>true</c>, TLS certificate validation is disabled on the underlying handler (for Zscaler / corporate-proxy environments). Default: <c>false</c>.</summary>
    public bool DisableConnectionVerification { get; }

    /// <summary>The default <see cref="RequestTimeout"/>: 100 s, the same as <see cref="HttpClient"/>'s own default.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(100);

    // CancellationTokenSource.CancelAfter rejects anything above int.MaxValue milliseconds (~24.8 days).
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    private TimeSpan _requestTimeout = DefaultRequestTimeout;

    /// <summary>
    /// Per-request time limit, enforced by the client. If no response arrives in time, the request throws
    /// <see cref="ApiTimeoutException"/>. Default: <see cref="DefaultRequestTimeout"/>. Use
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subclasses whose calls run long, such as replace-on-ingest uploads, should raise this in their
    /// constructor. Callers can also change it per instance.
    /// </para>
    /// <para>
    /// <strong>Injected <see cref="HttpClient"/>s keep their own <see cref="HttpClient.Timeout"/>.</strong>
    /// The client does not change a <see cref="HttpClient"/> it does not own, so the shorter of the two
    /// limits wins. A plain <c>new HttpClient()</c> stops every request at 100 s. When that happens the
    /// thrown <see cref="ApiTimeoutException"/> has <see cref="ApiTimeoutException.IsHttpClientTimeout"/>
    /// set and its message says so. A client constructed without an injected <see cref="HttpClient"/>
    /// disables its own <see cref="HttpClient.Timeout"/>, so only this value applies.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Not positive and not infinite, or above ~24.8 days.</exception>
    public TimeSpan RequestTimeout
    {
        get => _requestTimeout;
        set
        {
            if (value != System.Threading.Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > MaxRequestTimeout))
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "RequestTimeout must be positive (at most ~24.8 days) or Timeout.InfiniteTimeSpan.");
            _requestTimeout = value;
        }
    }

    private readonly bool _disposeHttp;

    /// <summary>
    /// Construct a client. Default: owns and disposes the HttpClient.
    /// Pass an existing <paramref name="http"/> to share an HttpClient
    /// across multiple clients (e.g. for shared connection pooling).
    /// </summary>
    /// <param name="baseUri">Base URL for all requests. Trailing slash optional.</param>
    /// <param name="credential">Auth credential. Use <see cref="ApiCredential.None"/> for unauthenticated APIs.</param>
    /// <param name="disableConnectionVerification">Disable TLS validation. For environments behind a TLS-intercepting proxy (Zscaler).</param>
    /// <param name="http">Optionally inject an HttpClient. The client takes ownership only if <paramref name="http"/> is null.</param>
    /// <param name="userAgent">User-Agent header. Default: <c>Tamp.Http/0.1.0</c>.</param>
    protected TampApiClient(
        Uri baseUri,
        ApiCredential credential,
        bool disableConnectionVerification = false,
        HttpClient? http = null,
        string? userAgent = null)
    {
        BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
        DisableConnectionVerification = disableConnectionVerification;

        if (http is null)
        {
            var handler = new HttpClientHandler();
            if (disableConnectionVerification)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            // RequestTimeout governs an owned client. HttpClient's built-in 100 s cap must not
            // fire first, or RequestTimeout could never be raised above it.
            Http = new HttpClient(handler) { BaseAddress = NormalizeBase(baseUri), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _disposeHttp = true;
        }
        else
        {
            Http = http;
            _disposeHttp = false;
            if (http.BaseAddress is null) http.BaseAddress = NormalizeBase(baseUri);
        }

        Http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? "Tamp.Http/0.1.0");
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>GET a JSON response and deserialize to <typeparamref name="T"/>.</summary>
    protected async Task<T> GetAsync<T>(string relativeUri, CancellationToken ct = default)
        => await SendJsonAsync<T>(HttpMethod.Get, relativeUri, body: null, ct).ConfigureAwait(false);

    /// <summary>POST a JSON body and deserialize the JSON response to <typeparamref name="T"/>.</summary>
    protected async Task<T> PostJsonAsync<T>(string relativeUri, object body, CancellationToken ct = default)
        => await SendJsonAsync<T>(HttpMethod.Post, relativeUri, body, ct).ConfigureAwait(false);

    /// <summary>POST a JSON body, discarding any response payload (204 / empty body).</summary>
    protected async Task PostJsonAsync(string relativeUri, object body, CancellationToken ct = default)
        => await SendJsonAsync(HttpMethod.Post, relativeUri, body, ct).ConfigureAwait(false);

    /// <summary>PUT a JSON body and deserialize the JSON response.</summary>
    protected async Task<T> PutJsonAsync<T>(string relativeUri, object body, CancellationToken ct = default)
        => await SendJsonAsync<T>(HttpMethod.Put, relativeUri, body, ct).ConfigureAwait(false);

    /// <summary>PUT a JSON body, discarding any response payload.</summary>
    protected async Task PutJsonAsync(string relativeUri, object body, CancellationToken ct = default)
        => await SendJsonAsync(HttpMethod.Put, relativeUri, body, ct).ConfigureAwait(false);

    /// <summary>PATCH a JSON body and deserialize the JSON response.</summary>
    protected async Task<T> PatchJsonAsync<T>(string relativeUri, object body, CancellationToken ct = default)
        => await SendJsonAsync<T>(HttpMethod.Patch, relativeUri, body, ct).ConfigureAwait(false);

    /// <summary>DELETE; discards any response payload.</summary>
    protected async Task DeleteAsync(string relativeUri, CancellationToken ct = default)
        => await SendJsonAsync(HttpMethod.Delete, relativeUri, body: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Low-level escape hatch: build the request yourself and get back the raw <see cref="HttpResponseMessage"/>.
    /// The auth header is applied automatically. The caller must check for success and dispose the response.
    /// </summary>
    /// <remarks>
    /// <see cref="RequestTimeout"/> and the timeout and transport diagnostics apply here too. With
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, the limit covers the request up to the response
    /// headers only, not reading the body afterwards.
    /// </remarks>
    protected async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        Credential.ApplyTo(request.Headers);
        var uri = request.RequestUri is null ? null
            : request.RequestUri.IsAbsoluteUri ? request.RequestUri.ToString()
            : ResolveRequestUri(request.RequestUri.OriginalString)?.ToString();
        var (response, _) = await SendWithDiagnosticsAsync(request, completion, uri, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Send under <see cref="RequestTimeout"/> and turn every failure that arrives with no response into an
    /// exception that states the elapsed time, the endpoint and the innermost cause (TAM-294). Returns the
    /// response with the time it took, for the HTTP-error message.
    /// </summary>
    private async Task<(HttpResponseMessage Response, TimeSpan Elapsed)> SendWithDiagnosticsAsync(
        HttpRequestMessage request, HttpCompletionOption completion, string? resolvedUri, CancellationToken ct)
    {
        var method = request.Method.Method;
        var endpoint = $"{method} {resolvedUri ?? request.RequestUri?.ToString() ?? "(no URI)"}";
        var limit = RequestTimeout;

        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        if (limit != System.Threading.Timeout.InfiniteTimeSpan) timeoutCts.CancelAfter(limit);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await Http.SendAsync(request, completion, linked.Token).ConfigureAwait(false);
            return (response, sw.Elapsed);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // The caller cancelled. Keep the caller's token and the type so `when (ct.IsCancellationRequested)`
            // filters keep matching, and record how long the request ran.
            throw new TaskCanceledException(
                $"{endpoint} was cancelled by the caller after {ApiFailureText.Seconds(sw.Elapsed)}.", ex, ct);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new ApiTimeoutException(method, resolvedUri, sw.Elapsed, limit, isHttpClientTimeout: false,
                $"{endpoint} timed out after {ApiFailureText.Seconds(sw.Elapsed)} with no response " +
                $"(RequestTimeout {ApiFailureText.Seconds(limit)}). {CauseClause(ex)}", ex);
        }
        catch (OperationCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // The injected HttpClient's own Timeout fired before RequestTimeout. The fix belongs to the
            // caller's HttpClient, so say that plainly.
            var hcLimit = Http.Timeout;
            throw new ApiTimeoutException(method, resolvedUri, sw.Elapsed, hcLimit, isHttpClientTimeout: true,
                $"{endpoint} timed out after {ApiFailureText.Seconds(sw.Elapsed)} with no response. The injected " +
                $"HttpClient's own Timeout ({ApiFailureText.Seconds(hcLimit)}) fired before RequestTimeout " +
                $"({ApiFailureText.Seconds(limit)}). To let RequestTimeout govern, set HttpClient.Timeout to " +
                $"Timeout.InfiniteTimeSpan or stop injecting the HttpClient. {CauseClause(ex)}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiTransportException(method, resolvedUri, sw.Elapsed,
                $"{endpoint} failed after {ApiFailureText.Seconds(sw.Elapsed)} before any response arrived: " +
                $"{ex.Message} {CauseClause(ex)}", ex);
        }
    }

    private static string CauseClause(Exception ex)
        => ApiFailureText.InnermostCause(ex) is { } cause
            ? $"Innermost cause: {cause}."
            : "No underlying cause reported (the request was still pending: a hang, not an error).";

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relativeUri, object? body, CancellationToken ct)
    {
        using var response = await SendInternalAsync(method, relativeUri, body, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (stream.CanSeek && stream.Length == 0)
            throw new ApiException(response.StatusCode, response.RequestMessage?.RequestUri?.ToString(), method.Method, null,
                $"Expected JSON body for {method.Method} {relativeUri} but response was empty.");
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        return value ?? throw new ApiException(response.StatusCode, response.RequestMessage?.RequestUri?.ToString(), method.Method, null,
            $"JSON for {method.Method} {relativeUri} deserialized to null.");
    }

    private async Task SendJsonAsync(HttpMethod method, string relativeUri, object? body, CancellationToken ct)
    {
        using var response = await SendInternalAsync(method, relativeUri, body, ct).ConfigureAwait(false);
        // No body expected; the caller is fine with whatever came back.
    }

    private async Task<HttpResponseMessage> SendInternalAsync(HttpMethod method, string relativeUri, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        Credential.ApplyTo(request.Headers);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        // Resolve the eventual request URI ourselves — HttpClient
        // populates RequestMessage on the response inconsistently
        // (test handlers in particular often skip it).
        var resolvedUri = ResolveRequestUri(relativeUri)?.ToString();

        var (response, elapsed) = await SendWithDiagnosticsAsync(request, HttpCompletionOption.ResponseContentRead, resolvedUri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var bodyText = await CaptureErrorBodyAsync(response, ct).ConfigureAwait(false);
            var msg = $"{method.Method} {resolvedUri ?? relativeUri} -> {(int)response.StatusCode} {response.ReasonPhrase} (after {ApiFailureText.Seconds(elapsed)})";
            var status = response.StatusCode;
            response.Dispose();
            throw status switch
            {
                >= HttpStatusCode.InternalServerError =>
                    new ApiServerException(status, resolvedUri, method.Method, bodyText, msg) { Elapsed = elapsed },
                _ =>
                    new ApiClientException(status, resolvedUri, method.Method, bodyText, msg) { Elapsed = elapsed },
            };
        }
        return response;
    }

    private Uri? ResolveRequestUri(string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out var abs)) return abs;
        var baseAddr = Http.BaseAddress;
        return baseAddr is null
            ? null
            : new Uri(baseAddr, relative.TrimStart('/'));
    }

    private async Task<string?> CaptureErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[MaxCapturedErrorBodyBytes];
            var total = 0;
            int read;
            while ((read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total == buf.Length) break;
            }
            return Encoding.UTF8.GetString(buf, 0, total);
        }
        catch
        {
            return null;
        }
    }

    private static Uri NormalizeBase(Uri u)
    {
        // HttpClient.BaseAddress requires a trailing slash for HttpClient.SendAsync to combine with relative URIs correctly.
        if (u.AbsoluteUri.EndsWith('/')) return u;
        return new Uri(u.AbsoluteUri + "/");
    }

    public void Dispose()
    {
        if (_disposeHttp) Http.Dispose();
        GC.SuppressFinalize(this);
    }
}
