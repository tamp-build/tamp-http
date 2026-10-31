# Changelog

All notable changes to **Tamp.Http** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] — 2026-10-31

TAM-294, from strata-home's nightly post-mortem. A dead network hang surfaced as
*"the configured HttpClient.Timeout of 100 seconds elapsing"*. That message gives the
limit, not the time spent, and it read as "payload too big". The real
`SocketException` only appeared after walking the inner-exception chain.

### Added

- **`TampApiClient.RequestTimeout`**: a per-request limit enforced by the client on every call, including `SendRawAsync`. Defaults to `DefaultRequestTimeout` (100 s). Settable per instance or by a subclass, and accepts `Timeout.InfiniteTimeSpan`. Rejects non-positive values and values beyond `CancelAfter`'s range.
- **`ApiTimeoutException`** (`TaskCanceledException` subclass, inner `TimeoutException`, matching `HttpClient`'s own shape): `RequestMethod`, `RequestUri`, `Elapsed`, `Timeout`, `IsHttpClientTimeout`.
- **`ApiTransportException`** (`HttpRequestException` subclass; original kept as inner, `HttpRequestError` and `StatusCode` preserved): `RequestMethod`, `RequestUri`, `Elapsed`.
- **`ApiException.Elapsed`**: time to the error response.

### Changed

- Every failure message now gives the **elapsed time**, the **endpoint** and the **innermost cause**. The innermost cause is the deepest exception that isn't a cancellation or timeout wrapper; for a `SocketException` it includes the `SocketError` code. When nothing below the wrappers exists, the message says the request was still pending, i.e. a hang rather than an error. Caller cancellation still throws `TaskCanceledException` on the caller's own token, now with the elapsed time.
- A timeout from an injected `HttpClient`'s own `Timeout` is named as such, with the fix: `HttpClient.Timeout = Timeout.InfiniteTimeSpan`, or don't inject the client. Injected clients are never modified.
- A client-owned `HttpClient` now has `Timeout = InfiniteTimeSpan`, so `RequestTimeout` governs. The effective default is unchanged at 100 s.
- HTTP-error messages end with `(after N.N s)`.

## [0.1.2] — 2026-09-27

### Added

- Package now ships XML documentation files (`.xml`) alongside the assembly, so consumers get IntelliSense and API docs. (Mirrors [tamp-build/tamp#3](https://github.com/tamp-build/tamp/pull/50).)


## [0.1.1] - 2026-05-12

### Added

- **`HttpProbe.WaitForHealthy(url, timeout, ...)`** — post-deploy smoke pattern. Polls a URL until a configurable predicate returns true or the timeout elapses. Defaults to 2-second polling and `IsSuccessStatusCode`. Optional overrides: `interval`, `headers`, `isHealthy` predicate (for body-content checks like rejecting `200 OK` with `"status": "degraded"` bodies), `HttpClient` (for self-signed certs / proxies / custom handlers), `CancellationToken`. Throws `TimeoutException` with diagnostic message (last status, attempt count, last transport error) when the budget runs out. Treats `HttpRequestException` and `HttpClient`-side timeouts as transient, retries through them. Companion to the `Tamp.Helm.V3` 0.1.0 cutover surface for SmokeQa targets that follow a Deploy.

## [0.1.0]

### Added

- Initial release. `TampApiClient` abstract base class for typed HTTP-API wrappers (GET/POST/PUT/PATCH/DELETE helpers with JSON serialization). `ApiCredential` + `ApiException` companion types. Auth credentials carrying a `Secret` join the runner's redaction table automatically.
