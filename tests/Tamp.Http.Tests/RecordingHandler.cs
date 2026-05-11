using System.Net;
using System.Net.Http.Headers;

namespace Tamp.Http.Tests;

/// <summary>
/// HttpMessageHandler that records every outgoing request and returns
/// scripted responses. We capture the request's method/uri/headers/body
/// into a snapshot object BEFORE HttpClient.SendAsync disposes the
/// underlying request — assertions inspect the snapshots, not the
/// disposed HttpRequestMessage.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType,
            body));
        return Responder(request);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    IDictionary<string, List<string>> Headers,
    MediaTypeHeaderValue? ContentType,
    string? Body);
