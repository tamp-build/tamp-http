namespace Tamp.Http.Tests;

/// <summary>
/// Minimal TampApiClient subclass used by the test suite to exercise
/// the foundation's protected surface (GET/POST/PUT/PATCH/DELETE +
/// SendRaw). Each method is a thin pass-through so tests can drive
/// any HTTP shape they want.
/// </summary>
internal sealed class TestApiClient : TampApiClient
{
    public TestApiClient(Uri baseUri, ApiCredential credential, HttpClient http)
        : base(baseUri, credential, http: http) { }

    public Task<T> Get<T>(string uri) => GetAsync<T>(uri);
    public Task<T> Post<T>(string uri, object body) => PostJsonAsync<T>(uri, body);
    public Task Post(string uri, object body) => PostJsonAsync(uri, body);
    public Task<T> Put<T>(string uri, object body) => PutJsonAsync<T>(uri, body);
    public Task Put(string uri, object body) => PutJsonAsync(uri, body);
    public Task<T> Patch<T>(string uri, object body) => PatchJsonAsync<T>(uri, body);
    public Task Delete(string uri) => DeleteAsync(uri);
    public Task<HttpResponseMessage> Raw(HttpRequestMessage request) => SendRawAsync(request);
}

internal sealed record SamplePayload(int Id, string Name);
internal sealed record SampleRequest(string Title);
