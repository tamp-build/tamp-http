using System.Net;
using System.Text;
using System.Text.Json;
using Tamp;
using Xunit;

namespace Tamp.Http.Tests;

public sealed class TampApiClientTests
{
    private static (TestApiClient Client, RecordingHandler Handler) MakeClient(
        Uri? baseUri = null,
        ApiCredential? cred = null,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;
        var http = new HttpClient(handler);
        var client = new TestApiClient(baseUri ?? new Uri("https://api.example.com"), cred ?? ApiCredential.None, http);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload)
    {
        var resp = new HttpResponseMessage(status);
        resp.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return resp;
    }

    // ---- BaseUri ----

    [Fact]
    public void BaseUri_Gets_Trailing_Slash()
    {
        var (client, _) = MakeClient(new Uri("https://api.example.com"));
        Assert.EndsWith("/", client.Http_BaseAddressForTest());
    }

    [Fact]
    public void BaseUri_Already_Trailing_Slash_Is_Unchanged()
    {
        var (client, _) = MakeClient(new Uri("https://api.example.com/"));
        Assert.Equal("https://api.example.com/", client.Http_BaseAddressForTest());
    }

    // ---- GET ----

    [Fact]
    public async Task Get_Deserializes_Json_Response()
    {
        var (client, handler) = MakeClient(
            responder: _ => JsonResponse(HttpStatusCode.OK, new { id = 42, name = "answer" }));

        var payload = await client.Get<SamplePayload>("things/42");
        Assert.Equal(42, payload.Id);
        Assert.Equal("answer", payload.Name);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Contains("things/42", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Get_PropertyNames_Match_Case_Insensitively()
    {
        var (client, _) = MakeClient(
            // Server sends PascalCase; client deserializes regardless.
            responder: _ => JsonResponse(HttpStatusCode.OK, new { Id = 1, Name = "x" }));
        var p = await client.Get<SamplePayload>("/");
        Assert.Equal(1, p.Id);
        Assert.Equal("x", p.Name);
    }

    // ---- POST ----

    [Fact]
    public async Task Post_Serializes_Body_As_Json()
    {
        var (client, handler) = MakeClient(
            responder: _ => JsonResponse(HttpStatusCode.Created, new { id = 7, name = "new" }));

        var result = await client.Post<SamplePayload>("things", new SampleRequest("Hello"));
        Assert.Equal(7, result.Id);

        var sent = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/json", sent.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(sent.Body!);
        Assert.Equal("Hello", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Post_Without_Return_Type_Discards_Body()
    {
        var (client, _) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.Post("things", new SampleRequest("x")); // no throw
    }

    // ---- PUT / PATCH / DELETE ----

    [Fact]
    public async Task Put_Uses_PUT_Method()
    {
        var (client, handler) = MakeClient(
            responder: _ => JsonResponse(HttpStatusCode.OK, new { id = 1, name = "n" }));
        await client.Put<SamplePayload>("things/1", new SampleRequest("x"));
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Patch_Uses_PATCH_Method()
    {
        var (client, handler) = MakeClient(
            responder: _ => JsonResponse(HttpStatusCode.OK, new { id = 1, name = "n" }));
        await client.Patch<SamplePayload>("things/1", new SampleRequest("x"));
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Delete_Uses_DELETE_Method()
    {
        var (client, handler) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.Delete("things/1");
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }

    // ---- auth headers ----

    [Fact]
    public async Task Auth_Header_Is_Applied_To_Every_Request()
    {
        var pat = new Secret("PAT", "secret-pat");
        var (client, handler) = MakeClient(
            cred: ApiCredential.BasicPat(pat),
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.Delete("a");
        await client.Delete("b");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(":secret-pat"));
        Assert.All(handler.Requests, r =>
        {
            Assert.True(r.Headers.TryGetValue("Authorization", out var auth));
            Assert.Single(auth!);
            Assert.Equal($"Basic {encoded}", auth![0]);
        });
    }

    [Fact]
    public async Task Bearer_Auth_Header_Is_Applied()
    {
        var token = new Secret("token", "jwt.fake.value");
        var (client, handler) = MakeClient(
            cred: ApiCredential.Bearer(token),
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await client.Delete("x");
        Assert.Equal("Bearer jwt.fake.value", handler.Requests[0].Headers["Authorization"][0]);
    }

    // ---- error mapping ----

    [Fact]
    public async Task Status_4xx_Throws_ApiClientException()
    {
        var (client, _) = MakeClient(
            responder: _ =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.NotFound);
                resp.Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json");
                return resp;
            });

        var ex = await Assert.ThrowsAsync<ApiClientException>(() => client.Get<SamplePayload>("/missing"));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("GET", ex.RequestMethod);
        Assert.Contains("missing", ex.RequestUri);
        Assert.Contains("not found", ex.ResponseBody);
        Assert.True(ex.IsClientError);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Status_5xx_Throws_ApiServerException_As_Transient()
    {
        var (client, _) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("server is sad", Encoding.UTF8, "text/plain"),
            });

        var ex = await Assert.ThrowsAsync<ApiServerException>(() => client.Get<SamplePayload>("/x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.True(ex.IsTransient);
        Assert.False(ex.IsClientError);
        Assert.Equal("server is sad", ex.ResponseBody);
    }

    [Fact]
    public async Task Error_Body_Is_Truncated_When_Larger_Than_Cap()
    {
        var huge = new string('x', 100_000);
        var (client, _) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(huge, Encoding.UTF8, "text/plain"),
            });
        client.MaxCapturedErrorBodyBytes = 256;

        var ex = await Assert.ThrowsAsync<ApiClientException>(() => client.Get<SamplePayload>("/x"));
        Assert.NotNull(ex.ResponseBody);
        Assert.True(ex.ResponseBody!.Length <= 256, $"Expected body <= 256 bytes, got {ex.ResponseBody.Length}");
    }

    // ---- raw escape hatch ----

    [Fact]
    public async Task SendRaw_Applies_Auth_And_Returns_Raw_Response()
    {
        var (client, handler) = MakeClient(
            cred: ApiCredential.Bearer(new Secret("t", "raw-token")),
            responder: _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("raw body") });

        using var req = new HttpRequestMessage(HttpMethod.Head, "raw");
        using var resp = await client.Raw(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("raw body", await resp.Content.ReadAsStringAsync());
        Assert.Equal("Bearer raw-token", handler.Requests[0].Headers["Authorization"][0]);
    }

    [Fact]
    public async Task SendRaw_Null_Throws()
    {
        var (client, _) = MakeClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.Raw(null!));
    }

    // ---- json shape ----

    [Fact]
    public async Task Default_Json_Options_Use_CamelCase_For_Serialization()
    {
        var (client, handler) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.Post("things", new { FirstName = "Sam", LastName = "Smith" });
        var body = handler.Requests[0].Body!;
        Assert.Contains("\"firstName\"", body);
        Assert.Contains("\"lastName\"", body);
        Assert.DoesNotContain("\"FirstName\"", body);
    }

    [Fact]
    public async Task Null_Properties_Are_Omitted_From_Serialization()
    {
        var (client, handler) = MakeClient(
            responder: _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.Post("x", new { Name = "thing", Description = (string?)null });
        var body = handler.Requests[0].Body!;
        Assert.DoesNotContain("description", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\":\"thing\"", body);
    }

    // ---- ctor guards ----

    [Fact]
    public void Constructor_Rejects_Nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new TestApiClient(null!, ApiCredential.None, new HttpClient()));
        Assert.Throws<ArgumentNullException>(() => new TestApiClient(new Uri("https://x"), null!, new HttpClient()));
    }
}

internal static class TestApiClientExtensions
{
    // Tests need to inspect the underlying HttpClient's BaseAddress to
    // verify trailing-slash normalization. The Http property is protected;
    // an extension-method bridge in the same assembly exposes it cleanly.
    public static string Http_BaseAddressForTest(this TestApiClient client)
    {
        var prop = typeof(TampApiClient).GetProperty("Http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var http = (HttpClient)prop!.GetValue(client)!;
        return http.BaseAddress!.AbsoluteUri;
    }
}
