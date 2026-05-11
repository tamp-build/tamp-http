using System.Net.Http.Headers;
using Tamp;
using Xunit;

namespace Tamp.Http.Tests;

public sealed class ApiCredentialTests
{
    [Fact]
    public void Basic_Combines_Username_And_Password_With_Colon()
    {
        var headers = new HttpRequestMessage().Headers;
        var cred = ApiCredential.Basic("ci-user", new Secret("p", "hunter2"));
        cred.ApplyTo(headers);

        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ci-user:hunter2"));
        Assert.NotNull(headers.Authorization);
        Assert.Equal("Basic", headers.Authorization!.Scheme);
        Assert.Equal(expected, headers.Authorization.Parameter);
    }

    [Fact]
    public void BasicPat_Uses_Empty_Username()
    {
        // Azure DevOps / GitHub fine-grained PATs: empty username, PAT
        // in the password slot. Encoded result is `:<pat>` (note leading colon).
        var headers = new HttpRequestMessage().Headers;
        var pat = new Secret("PAT", "fake-pat-value");
        var cred = ApiCredential.BasicPat(pat);
        cred.ApplyTo(headers);

        var encoded = headers.Authorization!.Parameter!;
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Equal(":fake-pat-value", decoded);
        Assert.Same(pat, cred.Secret);
    }

    [Fact]
    public void Bearer_Emits_Authorization_Bearer()
    {
        var headers = new HttpRequestMessage().Headers;
        var token = new Secret("OAuth", "ya29.AAAA.fake");
        var cred = ApiCredential.Bearer(token);
        cred.ApplyTo(headers);

        Assert.Equal("Bearer", headers.Authorization!.Scheme);
        Assert.Equal("ya29.AAAA.fake", headers.Authorization.Parameter);
        Assert.Same(token, cred.Secret);
    }

    [Fact]
    public void CustomHeader_Adds_Header_With_Secret_Value()
    {
        var headers = new HttpRequestMessage().Headers;
        var key = new Secret("API key", "sk-test-123");
        var cred = ApiCredential.CustomHeader("X-Api-Key", key);
        cred.ApplyTo(headers);

        Assert.True(headers.Contains("X-Api-Key"));
        Assert.Contains("sk-test-123", headers.GetValues("X-Api-Key"));
        Assert.Same(key, cred.Secret);
    }

    [Fact]
    public void CustomHeader_Replaces_Previous_Value_On_Reapply()
    {
        var headers = new HttpRequestMessage().Headers;
        var k1 = new Secret("k", "v1");
        var k2 = new Secret("k", "v2");
        ApiCredential.CustomHeader("X-Api-Key", k1).ApplyTo(headers);
        ApiCredential.CustomHeader("X-Api-Key", k2).ApplyTo(headers);
        Assert.Single(headers.GetValues("X-Api-Key"));
        Assert.Contains("v2", headers.GetValues("X-Api-Key"));
    }

    [Fact]
    public void None_Adds_No_Auth_Header()
    {
        var headers = new HttpRequestMessage().Headers;
        ApiCredential.None.ApplyTo(headers);
        Assert.Null(headers.Authorization);
        Assert.Null(ApiCredential.None.Secret);
    }

    [Fact]
    public void Constructors_Reject_Nulls()
    {
        var s = new Secret("x", "y");
        Assert.Throws<ArgumentNullException>(() => ApiCredential.Basic(null!, s));
        Assert.Throws<ArgumentNullException>(() => ApiCredential.Basic("u", null!));
        Assert.Throws<ArgumentNullException>(() => ApiCredential.BasicPat(null!));
        Assert.Throws<ArgumentNullException>(() => ApiCredential.Bearer(null!));
        Assert.Throws<ArgumentNullException>(() => ApiCredential.CustomHeader(null!, s));
        Assert.Throws<ArgumentNullException>(() => ApiCredential.CustomHeader("h", null!));
    }
}
