using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Ralven.App.Services;
using Xunit;

namespace Ralven.Tests.App;

/// <summary>
/// The real browser handoff remains manual. The deterministic security
/// boundaries around it — PKCE/nonce construction, callback correlation and
/// token claim checks before Firebase receives the assertion — stay covered
/// here without a Google account or network access.
/// </summary>
public sealed class GoogleOAuthClientTests
{
    [Fact]
    public void IsConfigured_IsFalseWithoutAClientId()
    {
        Assert.False(new GoogleOAuthClient(null).IsConfigured);
        Assert.False(new GoogleOAuthClient(string.Empty).IsConfigured);
        Assert.False(new GoogleOAuthClient("   ").IsConfigured);
    }

    [Fact]
    public void IsConfigured_IsTrueWithAClientId()
    {
        Assert.True(new GoogleOAuthClient("1234-abc.apps.googleusercontent.com").IsConfigured);
    }

    [Fact]
    public async Task AuthenticateAsync_Unconfigured_FailsWithoutTouchingTheNetwork()
    {
        var called = false;
        using var client = new HttpClient(new ThrowingHandler(() => called = true));
        var oauth = new GoogleOAuthClient(client, clientId: null, clientSecret: null);

        var ticket = await oauth.AuthenticateAsync(cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Null(ticket.IdToken);
        Assert.False(string.IsNullOrWhiteSpace(ticket.Error));
        Assert.False(called);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("es")]
    public async Task AuthenticateAsync_Unconfigured_UsesTheSelectedLanguage(string cultureName)
    {
        var localization = new LocalizationService(System.Globalization.CultureInfo.GetCultureInfo(cultureName));
        using var client = new HttpClient(new ThrowingHandler(() => { }));
        var oauth = new GoogleOAuthClient(client, clientId: null, clientSecret: null, localization);

        var ticket = await oauth.AuthenticateAsync(cancellationToken: global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(localization["Account.Google.NotConfigured"], ticket.Error);
        Assert.NotEqual("Account.Google.NotConfigured", ticket.Error);
    }

    [Fact]
    public void BuildAuthorizeUrl_BindsPkceStateAndNonce()
    {
        using var client = new HttpClient(new ThrowingHandler(() => { }));
        var oauth = new GoogleOAuthClient(client, "client-id", clientSecret: null);

        var url = new Uri(oauth.BuildAuthorizeUrl("http://127.0.0.1:1234/", "verifier", "state", "nonce"));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal("state", query["state"]);
        Assert.Equal("nonce", query["nonce"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes("verifier"))), query["code_challenge"]);
    }

    [Fact]
    public void TryParseCallback_IgnoresWrongStateAndAcceptsTheValidCallback()
    {
        Assert.False(GoogleOAuthClient.TryParseCallback(
            "/?code=attacker-code&state=wrong", "expected", out _, out _));

        Assert.True(GoogleOAuthClient.TryParseCallback(
            "/?code=real-code&state=expected", "expected", out var code, out var error));
        Assert.Equal("real-code", code);
        Assert.Null(error);
    }

    [Fact]
    public void HasExpectedIdTokenClaims_RequiresIssuerAudienceExpirySubjectAndNonce()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var valid = new Dictionary<string, object?>
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = "client-id",
            ["exp"] = now.ToUnixTimeSeconds() + 60,
            ["sub"] = "google-subject",
            ["nonce"] = "expected-nonce",
        };

        Assert.True(GoogleOAuthClient.HasExpectedIdTokenClaims(
            IdToken(valid), "client-id", "expected-nonce", now));

        foreach (var invalid in new[]
        {
            Changed(valid, "iss", "https://issuer.example"),
            Changed(valid, "aud", "other-client"),
            Changed(valid, "exp", now.ToUnixTimeSeconds()),
            Changed(valid, "sub", string.Empty),
            Changed(valid, "sub", "   "),
            Changed(valid, "nonce", "wrong-nonce"),
        })
        {
            Assert.False(GoogleOAuthClient.HasExpectedIdTokenClaims(
                IdToken(invalid), "client-id", "expected-nonce", now));
        }

        Assert.False(GoogleOAuthClient.HasExpectedIdTokenClaims(
            "not-a-jwt", "client-id", "expected-nonce", now));
    }

    [Fact]
    public async Task ExchangeCodeAsync_RejectsAnIdTokenWithTheWrongNonce()
    {
        var now = DateTimeOffset.UtcNow;
        var token = IdToken(new Dictionary<string, object?>
        {
            ["iss"] = "accounts.google.com",
            ["aud"] = new[] { "client-id" },
            ["exp"] = now.ToUnixTimeSeconds() + 60,
            ["sub"] = "google-subject",
            ["nonce"] = "wrong-nonce",
        });
        using var client = new HttpClient(new ResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id_token = token }), Encoding.UTF8, "application/json"),
            }));
        var localization = new LocalizationService(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        var oauth = new GoogleOAuthClient(client, "client-id", clientSecret: null, localization);

        var ticket = await oauth.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/", "expected-nonce",
            global::Xunit.TestContext.Current.CancellationToken);

        Assert.Null(ticket.IdToken);
        Assert.Equal(localization["Account.Google.InvalidResponse"], ticket.Error);
    }

    [Fact]
    public async Task ExchangeCodeAsync_ReturnsOnlyACorrelatedIdTokenForFirebaseValidation()
    {
        var token = IdToken(new Dictionary<string, object?>
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = "client-id",
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
            ["sub"] = "google-subject",
            ["nonce"] = "expected-nonce",
        });
        using var client = new HttpClient(new ResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { id_token = token }), Encoding.UTF8, "application/json"),
            }));
        var oauth = new GoogleOAuthClient(client, "client-id", clientSecret: null);

        var ticket = await oauth.ExchangeCodeAsync(
            "code", "verifier", "http://127.0.0.1:1234/", "expected-nonce",
            global::Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(token, ticket.IdToken);
        Assert.Null(ticket.Error);
    }

    private static Dictionary<string, object?> Changed(
        Dictionary<string, object?> source,
        string key,
        object? value)
    {
        var copy = new Dictionary<string, object?>(source) { [key] = value };
        return copy;
    }

    private static string IdToken(Dictionary<string, object?> payload) =>
        $"{Base64Url(Encoding.UTF8.GetBytes("{}"))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload))}.signature";

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A página de retorno do OAuth é um recurso incorporado: se o arquivo sair
    /// do `.csproj` ou o nome lógico mudar, o navegador recebe uma exceção no
    /// meio de um login que já deu certo, e só em tempo de execução.
    /// </summary>
    [Fact]
    public void OAuthCallbackTemplate_IsEmbeddedAndCarriesEveryPlaceholder()
    {
        using var stream = typeof(GoogleOAuthClient).Assembly
            .GetManifestResourceStream("Ralven.App.Resources.oauth-callback.html");

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var template = reader.ReadToEnd();
        Assert.StartsWith("<!doctype html>", template, StringComparison.Ordinal);
        Assert.Contains("{culture}", template, StringComparison.Ordinal);
        Assert.Contains("{icon}", template, StringComparison.Ordinal);
        Assert.Contains("{body}", template, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler(Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
