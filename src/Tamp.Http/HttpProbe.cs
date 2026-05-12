using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tamp.Http;

/// <summary>
/// Build-script-friendly HTTP polling helpers for post-deploy smoke checks.
/// Lives in <c>Tamp.Http</c> 0.1.1+ to give targets a one-liner for
/// "wait until the deployed surface answers".
/// </summary>
/// <remarks>
/// <para>
/// The canonical use case is a SmokeQa target that runs after Deploy and waits
/// for the external URL (Cloudflare tunnel, k8s Ingress, ALB, etc.) to start
/// returning 2xx. The pattern HoldFast asked for in the 1.3.0 cutover review:
/// </para>
/// <code>
/// Target SmokeQa => _ => _
///     .DependsOn(DeployQa)
///     .Executes(async () =>
///         await HttpProbe.WaitForHealthy(
///             url: "https://qa.holdfast.lab/health/live",
///             timeout: TimeSpan.FromMinutes(2)));
/// </code>
/// <para>
/// Defaults: 2-second polling interval, <see cref="HttpResponseMessage.IsSuccessStatusCode"/>
/// as the health predicate, no custom headers, internally-managed HttpClient.
/// Override any of those for self-signed-cert dev environments, auth-required
/// health endpoints, or body-content checks (e.g., "200 OK with a 'degraded'
/// body should keep polling").
/// </para>
/// </remarks>
public static class HttpProbe
{
    /// <summary>
    /// Poll <paramref name="url"/> until <paramref name="isHealthy"/> returns true
    /// or <paramref name="timeout"/> elapses. Throws <see cref="TimeoutException"/>
    /// on timeout with a diagnostic message including the last observed status
    /// and attempt count.
    /// </summary>
    /// <param name="url">Target URL to GET each poll.</param>
    /// <param name="timeout">Total wall-clock budget for the wait. Required — no implicit default.</param>
    /// <param name="interval">Poll interval. Defaults to 2 seconds.</param>
    /// <param name="headers">Optional request headers (e.g., Authorization for non-public health endpoints).</param>
    /// <param name="isHealthy">
    /// Async predicate over each response. Defaults to <see cref="HttpResponseMessage.IsSuccessStatusCode"/>.
    /// Override to read the body — e.g., consider a 200 with body <c>{"status":"degraded"}</c> unhealthy.
    /// Returning <see cref="ValueTask{TResult}"/> keeps body inspection asynchronous; for pure status checks,
    /// return <c>ValueTask.FromResult(...)</c> or use an <c>async</c> lambda.
    /// </param>
    /// <param name="httpClient">
    /// Optional pre-configured client. Pass one if you need self-signed-cert tolerance,
    /// proxy config, or custom handlers. When null, a default client is created and disposed internally.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation. Honored on every iteration boundary.</param>
    public static async Task WaitForHealthy(
        string url,
        TimeSpan timeout,
        TimeSpan? interval = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Func<HttpResponseMessage, ValueTask<bool>>? isHealthy = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is required.", nameof(url));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        var pollInterval = interval ?? TimeSpan.FromSeconds(2);
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive when set.");

        var check = isHealthy ?? (r => ValueTask.FromResult(r.IsSuccessStatusCode));

        var ownClient = httpClient is null;
        var http = httpClient ?? new HttpClient();
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            var attempts = 0;
            System.Net.HttpStatusCode? lastStatus = null;
            string? lastError = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (headers is not null)
                    {
                        foreach (var (k, v) in headers)
                            request.Headers.TryAddWithoutValidation(k, v);
                    }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    lastStatus = response.StatusCode;
                    if (await check(response).ConfigureAwait(false)) return;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex.Message;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient's per-request timeout fired (not our overall budget) — treat as unhealthy attempt and retry.
                    lastError = "Request timed out (HttpClient.Timeout).";
                }

                var now = DateTime.UtcNow;
                if (now >= deadline) break;

                var remaining = deadline - now;
                var waitFor = pollInterval < remaining ? pollInterval : remaining;
                if (waitFor > TimeSpan.Zero)
                    await Task.Delay(waitFor, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"WaitForHealthy({url}) gave up after {timeout.TotalSeconds:F0}s and {attempts} attempt(s). " +
                $"Last status: {(lastStatus.HasValue ? $"{(int)lastStatus.Value} {lastStatus.Value}" : "no response")}. " +
                (lastError is not null ? $"Last transport error: {lastError}" : "No transport errors observed."));
        }
        finally
        {
            if (ownClient) http.Dispose();
        }
    }
}
