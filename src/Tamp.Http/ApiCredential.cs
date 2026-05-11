using System.Net.Http.Headers;
using System.Text;

namespace Tamp.Http;

/// <summary>
/// Authentication credential for an HTTP API. Concrete subclasses
/// build the appropriate <see cref="HttpRequestHeaders"/> entries
/// (Basic, Bearer, custom header). Credential implementations are
/// responsible for keeping the underlying <see cref="Secret"/>
/// joined to the redaction table.
/// </summary>
public abstract class ApiCredential
{
    /// <summary>The secret payload backing this credential. Implementations register it with the redaction table when the client is constructed.</summary>
    public abstract Secret? Secret { get; }

    /// <summary>Apply this credential to an outgoing request's headers.</summary>
    public abstract void ApplyTo(HttpRequestHeaders headers);

    /// <summary>HTTP Basic auth with username + password. The password joins the redaction table.</summary>
    public static ApiCredential Basic(string username, Secret password) => new BasicCredential(username, password);

    /// <summary>HTTP Basic auth with PAT only — username is empty, PAT goes in the password slot. Standard for Azure DevOps PATs and GitHub fine-grained tokens.</summary>
    public static ApiCredential BasicPat(Secret pat) => new BasicCredential(string.Empty, pat);

    /// <summary>Bearer token (OAuth 2 / JWT / similar).</summary>
    public static ApiCredential Bearer(Secret token) => new BearerCredential(token);

    /// <summary>Arbitrary custom header (e.g. <c>X-Api-Key: ...</c>).</summary>
    public static ApiCredential CustomHeader(string headerName, Secret value) => new CustomHeaderCredential(headerName, value);

    /// <summary>No authentication. Useful for unauthenticated endpoints or transports that auth elsewhere (e.g. a mutual-TLS HttpClient).</summary>
    public static ApiCredential None { get; } = new NoneCredential();
}

internal sealed class BasicCredential : ApiCredential
{
    private readonly string _username;
    private readonly Secret _password;
    public BasicCredential(string username, Secret password)
    {
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }
    public override Secret? Secret => _password;
    public override void ApplyTo(HttpRequestHeaders headers)
    {
        var raw = $"{_username}:{_password.Reveal()}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
    }
}

internal sealed class BearerCredential : ApiCredential
{
    private readonly Secret _token;
    public BearerCredential(Secret token) => _token = token ?? throw new ArgumentNullException(nameof(token));
    public override Secret? Secret => _token;
    public override void ApplyTo(HttpRequestHeaders headers)
    {
        headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Reveal());
    }
}

internal sealed class CustomHeaderCredential : ApiCredential
{
    private readonly string _name;
    private readonly Secret _value;
    public CustomHeaderCredential(string name, Secret value)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }
    public override Secret? Secret => _value;
    public override void ApplyTo(HttpRequestHeaders headers)
    {
        if (headers.Contains(_name)) headers.Remove(_name);
        headers.Add(_name, _value.Reveal());
    }
}

internal sealed class NoneCredential : ApiCredential
{
    public override Secret? Secret => null;
    public override void ApplyTo(HttpRequestHeaders headers) { /* no-op */ }
}
