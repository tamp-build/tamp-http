using System.Net;

namespace Tamp.Http;

/// <summary>
/// Thrown when an HTTP request reaches a remote API but the response
/// indicates an error. Carries the status code, the request URI, the
/// response body (truncated if huge), and — when the server returned
/// JSON — a parsed error envelope.
/// </summary>
public class ApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? RequestUri { get; }
    public string? RequestMethod { get; }
    public string? ResponseBody { get; }

    public ApiException(
        HttpStatusCode statusCode,
        string? requestUri,
        string? requestMethod,
        string? responseBody,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RequestUri = requestUri;
        RequestMethod = requestMethod;
        ResponseBody = responseBody;
    }

    /// <summary>True when the status code is 5xx — caller may want to retry.</summary>
    public bool IsTransient => (int)StatusCode >= 500 && (int)StatusCode < 600;

    /// <summary>True when the status code is 4xx — caller should not retry without changing the request.</summary>
    public bool IsClientError => (int)StatusCode >= 400 && (int)StatusCode < 500;
}

/// <summary>4xx — caller fault (bad request, unauthorized, forbidden, not found, conflict, etc.).</summary>
public sealed class ApiClientException : ApiException
{
    public ApiClientException(HttpStatusCode statusCode, string? requestUri, string? requestMethod, string? responseBody, string message, Exception? inner = null)
        : base(statusCode, requestUri, requestMethod, responseBody, message, inner) { }
}

/// <summary>5xx — server fault. Generally safe to retry with backoff.</summary>
public sealed class ApiServerException : ApiException
{
    public ApiServerException(HttpStatusCode statusCode, string? requestUri, string? requestMethod, string? responseBody, string message, Exception? inner = null)
        : base(statusCode, requestUri, requestMethod, responseBody, message, inner) { }
}
