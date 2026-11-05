using System.Net;
using System.Security.Cryptography;
using System.Text;
using DocProc.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Tests;

/// <summary>
/// <c>Authentication:Oidc:MetadataAddress</c> (docs/09 "With Docker Compose"): the browser signs in at the public issuer
/// (<c>http://localhost:8080/realms/docproc</c>), which the API's container cannot reach, so the API fetches the discovery
/// document and the signing keys over a back channel while it keeps validating the public issuer.
/// </summary>
public sealed class OidcMetadataAddressTests
{
    private const string PublicIssuer = "http://localhost:8080/realms/docproc";
    private const string BackChannel = "http://keycloak:8080/realms/docproc";
    private const string MetadataAddress = BackChannel + "/.well-known/openid-configuration";
    private const string Audience = "docproc-api";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Metadata_comes_from_the_back_channel_while_the_issuer_stays_the_authority()
    {
        using var provider = Build(new() { ["Authentication:Oidc:Authority"] = PublicIssuer, ["Authentication:Oidc:MetadataAddress"] = MetadataAddress, ["Authentication:Oidc:RequireHttpsMetadata"] = "false" });

        var options = BearerOptions(provider);

        options.Authority.ShouldBe(PublicIssuer);
        options.MetadataAddress.ShouldBe(MetadataAddress);
        options.TokenValidationParameters.ValidIssuer.ShouldBe(PublicIssuer);
    }

    [Fact]
    public void Without_a_metadata_address_the_discovery_document_is_read_from_the_authority()
    {
        using var provider = Build(new() { ["Authentication:Oidc:Authority"] = PublicIssuer, ["Authentication:Oidc:RequireHttpsMetadata"] = "false" });

        var options = BearerOptions(provider);

        options.MetadataAddress.ShouldBe(PublicIssuer + "/.well-known/openid-configuration");
        options.TokenValidationParameters.ValidIssuer.ShouldBe(PublicIssuer);
    }

    [Theory]
    [InlineData(null, MetadataAddress)]
    [InlineData("", MetadataAddress)]
    [InlineData(PublicIssuer, "keycloak/realms/docproc")]
    [InlineData(PublicIssuer, "/realms/docproc/.well-known/openid-configuration")]
    [InlineData(PublicIssuer, "ftp://keycloak/realms/docproc")]
    public void A_metadata_address_without_an_issuer_or_that_is_not_an_http_url_stops_the_start_up(string? authority, string metadataAddress)
    {
        var settings = new Dictionary<string, string?> { ["Authentication:Oidc:Authority"] = authority, ["Authentication:Oidc:MetadataAddress"] = metadataAddress };

        var error = Should.Throw<InvalidOperationException>(() => Build(settings).Dispose());

        error.Message.ShouldContain("Authentication:Oidc:MetadataAddress");
    }

    [Fact]
    public void Dev_authentication_ignores_the_metadata_address()
    {
        using var provider = Build(new() { ["Authentication:Dev:Enabled"] = "true", ["Authentication:Oidc:MetadataAddress"] = "not a url" });

        BearerOptions(provider).Authority.ShouldBeNull();
    }

    /// <summary>
    /// The compose topology in miniature: only the back channel answers (the public issuer would be the API's own
    /// container), the discovery document there names the public issuer, as Keycloak does with <c>KC_HOSTNAME</c> set,
    /// and a token issued by the public issuer validates. A token from any other issuer is still refused.
    /// </summary>
    [Fact]
    public async Task A_token_of_the_public_issuer_validates_against_keys_fetched_over_the_back_channel()
    {
        using var key = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(key) { KeyId = "kc-key" };
        using var keycloak = new BackChannelKeycloak(signingKey);
        using var provider = Build(
            new()
            {
                ["Authentication:Oidc:Authority"] = PublicIssuer,
                ["Authentication:Oidc:MetadataAddress"] = MetadataAddress,
                ["Authentication:Oidc:RequireHttpsMetadata"] = "false",
            },
            services => services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.BackchannelHttpHandler = keycloak));

        var accepted = await AuthenticateAsync(provider, Token(signingKey, PublicIssuer));
        var foreign = await AuthenticateAsync(provider, Token(signingKey, "http://elsewhere:8080/realms/docproc"));

        accepted.Succeeded.ShouldBeTrue(accepted.Failure?.Message);
        accepted.Principal!.FindFirst("preferred_username")!.Value.ShouldBe("tenant-a-admin");
        foreign.Succeeded.ShouldBeFalse();
        foreign.Failure.ShouldBeOfType<SecurityTokenInvalidIssuerException>();
        keycloak.Requested.ShouldNotBeEmpty();
        keycloak.Requested.ShouldAllBe(uri => uri.StartsWith(BackChannel, StringComparison.Ordinal));
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        var environment = new HostingEnvironment { EnvironmentName = "Testing", ContentRootPath = AppContext.BaseDirectory };
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(environment);
        services.AddDocProcAuthentication(configuration, environment);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static JwtBearerOptions BearerOptions(IServiceProvider provider) =>
        provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

    private static async Task<AuthenticateResult> AuthenticateAsync(IServiceProvider provider, string token)
    {
        await using var scope = provider.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {token}";
        return await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }

    private static string Token(SecurityKey key, string issuer) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = issuer,
        Audience = Audience,
        Expires = DateTime.UtcNow.AddMinutes(5),
        Claims = new Dictionary<string, object> { ["sub"] = "tenant-a-admin", ["preferred_username"] = "tenant-a-admin" },
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
    });

    /// <summary>Answers the discovery document and the key set at the back-channel host only; anything else is unreachable.</summary>
    private sealed class BackChannelKeycloak(RsaSecurityKey key) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            lock (Requested)
            {
                Requested.Add(uri);
            }

            var body = uri switch
            {
                MetadataAddress => $$"""{"issuer":"{{PublicIssuer}}","jwks_uri":"{{BackChannel}}/protocol/openid-connect/certs","id_token_signing_alg_values_supported":["RS256"]}""",
                BackChannel + "/protocol/openid-connect/certs" => Jwks(),
                _ => null,
            };

            return Task.FromResult(body is null
                ? throw new HttpRequestException($"{uri} is not reachable from the API's container")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private string Jwks()
        {
            var parameters = key.Rsa.ExportParameters(includePrivateParameters: false);
            return $$"""{"keys":[{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{key.KeyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}]}""";
        }
    }
}
