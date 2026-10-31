using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;

namespace Tamp.Http;

/// <summary>
/// Thrown when a request made through <see cref="TampApiClient"/> gets no response before a time limit
/// elapses: either the client's own <see cref="TampApiClient.RequestTimeout"/> or the
/// <see cref="HttpClient.Timeout"/> of an injected <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="TaskCanceledException"/> and carries a <see cref="TimeoutException"/> as its
/// <see cref="Exception.InnerException"/>, the same shape <see cref="HttpClient"/> uses for its own
/// timeouts. Existing <c>catch (TaskCanceledException)</c> retry logic keeps working.
/// </para>
/// <para>
/// The message gives the <strong>elapsed time</strong> as well as the limit, plus the endpoint and the
/// innermost cause. "Timed out after 102.1 s (limit 100 s)" points to a hang. A message that gives only
/// the limit reads like "the payload was too big" (TAM-294).
/// </para>
/// </remarks>
public sealed class ApiTimeoutException : TaskCanceledException
{
    /// <summary>HTTP method of the request that timed out.</summary>
    public string RequestMethod { get; }

    /// <summary>Resolved request URI, when known.</summary>
    public string? RequestUri { get; }

    /// <summary>Wall-clock time between sending the request and giving up.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The limit that fired.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// <c>true</c> when the injected <see cref="HttpClient"/>'s own <see cref="HttpClient.Timeout"/> fired
    /// instead of <see cref="TampApiClient.RequestTimeout"/>. The fix is then on the caller's
    /// <see cref="HttpClient"/>, not on the client.
    /// </summary>
    public bool IsHttpClientTimeout { get; }

    /// <summary>Construct the exception. <paramref name="cause"/> becomes the inner <see cref="TimeoutException"/>'s inner exception.</summary>
    public ApiTimeoutException(
        string requestMethod,
        string? requestUri,
        TimeSpan elapsed,
        TimeSpan timeout,
        bool isHttpClientTimeout,
        string message,
        Exception? cause = null)
        : base(message, new TimeoutException(message, cause))
    {
        RequestMethod = requestMethod;
        RequestUri = requestUri;
        Elapsed = elapsed;
        Timeout = timeout;
        IsHttpClientTimeout = isHttpClientTimeout;
    }
}

/// <summary>
/// Thrown when a request made through <see cref="TampApiClient"/> fails below HTTP, before any response
/// arrives. Examples: DNS failure, connection refused or timed out, TLS failure, connection reset.
/// </summary>
/// <remarks>
/// Derives from <see cref="HttpRequestException"/> and keeps the original exception as
/// <see cref="Exception.InnerException"/>, with its <see cref="HttpRequestException.HttpRequestError"/>
/// and <see cref="HttpRequestException.StatusCode"/> preserved. Existing <c>catch (HttpRequestException)</c>
/// logic keeps working. The message adds the elapsed time, the endpoint and the innermost cause, which is
/// usually a <see cref="SocketException"/> buried two or three levels down (TAM-294).
/// </remarks>
public sealed class ApiTransportException : HttpRequestException
{
    /// <summary>HTTP method of the failed request.</summary>
    public string RequestMethod { get; }

    /// <summary>Resolved request URI, when known.</summary>
    public string? RequestUri { get; }

    /// <summary>Wall-clock time between sending the request and the failure.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Construct the exception from the original transport failure.</summary>
    public ApiTransportException(
        string requestMethod,
        string? requestUri,
        TimeSpan elapsed,
        string message,
        HttpRequestException original)
        : base((original ?? throw new ArgumentNullException(nameof(original))).HttpRequestError, message, original, original.StatusCode)
    {
        RequestMethod = requestMethod;
        RequestUri = requestUri;
        Elapsed = elapsed;
    }
}

/// <summary>Message formatting shared by the <see cref="TampApiClient"/> failure paths.</summary>
internal static class ApiFailureText
{
    /// <summary>Seconds to one decimal place, culture-invariant: <c>102.1 s</c>.</summary>
    public static string Seconds(TimeSpan t)
        => t == System.Threading.Timeout.InfiniteTimeSpan
            ? "infinite"
            : t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// Describe the deepest <em>meaningful</em> inner exception as <c>Type: message</c>. A
    /// <see cref="SocketException"/> also gets its <see cref="SocketError"/> code.
    /// </summary>
    /// <remarks>
    /// Cancellation and timeout wrappers (<see cref="OperationCanceledException"/>,
    /// <see cref="TimeoutException"/>) are skipped. <see cref="HttpClient"/> nests them inside each other,
    /// and "A task was canceled." says nothing about why. Returns <c>null</c> when the chain below
    /// <paramref name="ex"/> holds only such wrappers or nothing at all. That means no underlying error was
    /// reported: the request was still pending when it was abandoned.
    /// </remarks>
    public static string? InnermostCause(Exception ex)
    {
        Exception? deepest = null;
        var e = Next(ex);
        while (e is not null)
        {
            if (e is not (OperationCanceledException or TimeoutException)) deepest = e;
            e = Next(e);
        }
        if (deepest is null) return null;

        var detail = deepest is SocketException se ? $"{se.Message} (SocketError.{se.SocketErrorCode})" : deepest.Message;
        return $"{deepest.GetType().Name}: {detail}";

        static Exception? Next(Exception x)
            => x is AggregateException { InnerExceptions.Count: > 0 } agg ? agg.InnerExceptions[0] : x.InnerException;
    }
}
