# Changelog

All notable changes to **Tamp.Http** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] — 2026-09-27

### Added

- Package now ships XML documentation files (`.xml`) alongside the assembly, so consumers get IntelliSense and API docs. (Mirrors [tamp-build/tamp#3](https://github.com/tamp-build/tamp/pull/50).)


## [0.1.1] - 2026-05-12

### Added

- **`HttpProbe.WaitForHealthy(url, timeout, ...)`** — post-deploy smoke pattern. Polls a URL until a configurable predicate returns true or the timeout elapses. Defaults to 2-second polling and `IsSuccessStatusCode`. Optional overrides: `interval`, `headers`, `isHealthy` predicate (for body-content checks like rejecting `200 OK` with `"status": "degraded"` bodies), `HttpClient` (for self-signed certs / proxies / custom handlers), `CancellationToken`. Throws `TimeoutException` with diagnostic message (last status, attempt count, last transport error) when the budget runs out. Treats `HttpRequestException` and `HttpClient`-side timeouts as transient, retries through them. Companion to the `Tamp.Helm.V3` 0.1.0 cutover surface for SmokeQa targets that follow a Deploy.

## [0.1.0]

### Added

- Initial release. `TampApiClient` abstract base class for typed HTTP-API wrappers (GET/POST/PUT/PATCH/DELETE helpers with JSON serialization). `ApiCredential` + `ApiException` companion types. Auth credentials carrying a `Secret` join the runner's redaction table automatically.
