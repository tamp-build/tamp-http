# Tamp.Http

Foundation library for HTTP-API wrappers in
[Tamp](https://github.com/tamp-build/tamp). The base class
`TampApiClient` handles HttpClient construction, Basic / Bearer /
custom-header auth with `Secret`-redacted credentials, JSON
serialization, and typed error mapping. Library-mode satellites
subclass it.

```csharp
using Tamp.Http;
```

| Package | Status |
|---|---|
| `Tamp.Http` | preview |

Requires `Tamp.Core ≥ 1.0.5`.

## Why this exists

Most Tamp satellites wrap a CLI binary and emit a `CommandPlan` —
that pattern fits `dotnet`, `docker`, `vite`, `playwright`. But some
useful tooling lives behind an HTTP API instead: Azure DevOps REST,
GitHub Issues, YouTrack, Jira, Linear. Each of those wants:

- A typed surface (PullRequests, Issues, Boards, Comments) — not
  argv-shape.
- Secret-protected auth (PAT, bearer, OAuth) — joining the redaction
  table.
- Sensible error mapping (4xx vs 5xx vs network) so build scripts
  can branch on intent, not status codes.
- JSON in / JSON out, idiomatic C# records.

Tamp.Http is the shared plumbing. Downstream satellites subclass
`TampApiClient` and don't think about HTTP — they think about their
domain.

## Subclass surface

```csharp
public sealed class AdoRestClient : TampApiClient
{
    public AdoRestClient(string orgUrl, Secret pat)
        : base(new Uri(orgUrl), ApiCredential.BasicPat(pat)) { }

    public PullRequestsApi PullRequests => new(this);

    public sealed class PullRequestsApi
    {
        private readonly AdoRestClient _c;
        internal PullRequestsApi(AdoRestClient c) => _c = c;

        public Task<PullRequest> GetByIdAsync(string project, string repo, int id, CancellationToken ct = default)
            => _c.GetForwarding<PullRequest>($"{project}/_apis/git/repositories/{repo}/pullrequests/{id}?api-version=7.1", ct);
    }

    // (`GetForwarding` is just `protected GetAsync` re-exposed for the inner class.)
}
```

## Build-script usage

```csharp
[Secret("ADO PAT", EnvironmentVariable = "ADO_PAT")]
readonly Secret AdoPat = null!;

Target FetchPr => _ => _.Executes(async () =>
{
    using var ado = new AdoRestClient("https://dev.azure.com/i3solutions/", AdoPat);
    var pr = await ado.PullRequests.GetByIdAsync("Strata", "Strata", id: 123);
    Console.WriteLine($"PR {pr.Id}: {pr.Title} by {pr.CreatedBy.DisplayName}");
});
```

Auth credential applies to every request automatically. The PAT
joins the redaction table at construction time — any log line that
echoes the value gets scrubbed.

## Post-deploy health probe (0.1.1+)

`HttpProbe.WaitForHealthy` is a static one-liner for SmokeQa-style targets that
need to wait for a deployed surface to start answering. No subclass needed.

```csharp
Target SmokeQa => _ => _
    .DependsOn(nameof(DeployQa))
    .Executes(async () =>
        await HttpProbe.WaitForHealthy(
            url: "https://qa.holdfast.lab/health/live",
            timeout: TimeSpan.FromMinutes(2)));
```

Defaults: 2-second polling, `IsSuccessStatusCode` as the predicate. Treats
`HttpRequestException` (connection refused, DNS, etc.) and per-request
`HttpClient` timeouts as transient — retries through them until the wall-clock
budget elapses. Throws `TimeoutException` on budget exhaustion with the last
observed status, the attempt count, and the last transport error in the message.

Override any default:

```csharp
await HttpProbe.WaitForHealthy(
    url: "https://qa.holdfast.lab/api/health",
    timeout: TimeSpan.FromMinutes(5),
    interval: TimeSpan.FromSeconds(5),                            // slower poll
    headers: new Dictionary<string, string>                       // auth-required endpoint
    {
        ["X-Probe-Key"] = ProbeKey.Reveal()                       // Secret + Reveal at call site
    },
    isHealthy: async r =>                                         // body-content check
    {
        if (!r.IsSuccessStatusCode) return false;
        var body = await r.Content.ReadAsStringAsync();
        return !body.Contains("\"status\":\"degraded\"");
    },
    cancellationToken: ct);
```

`HttpClient` override is for self-signed certs, corporate proxy handlers, or
shared client reuse — pass a pre-configured client and `WaitForHealthy` won't
dispose it.

## API credentials

```csharp
ApiCredential.Basic(string username, Secret password)   // standard HTTP Basic
ApiCredential.BasicPat(Secret pat)                       // empty username; ADO/GH style
ApiCredential.Bearer(Secret token)                       // OAuth 2 / JWT
ApiCredential.CustomHeader(string name, Secret value)    // X-Api-Key: ..., etc.
ApiCredential.None                                       // unauthenticated endpoints
```

## Errors

| Type | When | Caller's move |
|---|---|---|
| `ApiClientException` (`ApiException` subclass) | 4xx response | Don't retry without changing the request. Inspect `.ResponseBody` for diagnostics. |
| `ApiServerException` | 5xx response | Generally safe to retry with backoff. `.IsTransient == true`. |
| (other exceptions) | network / DNS / TLS / cancellation | Standard HttpClient exception types — `HttpRequestException`, `TaskCanceledException`, etc. |

Response body for error responses is captured into `.ResponseBody`,
truncated to `MaxCapturedErrorBodyBytes` (default 16 KiB).

## JSON shape

- **Serialization**: camelCase property names, nulls omitted.
- **Deserialization**: case-insensitive property matching.
- Subclasses can override `JsonOptions` to add converters (e.g. for
  enum string-mapping or custom datetime formats).

## TLS / corporate-proxy environments

Pass `disableConnectionVerification: true` to bypass cert validation
— for Zscaler / TLS-intercepting proxies. Default is off.

## Releasing

See [MAINTAINERS.md](MAINTAINERS.md).
