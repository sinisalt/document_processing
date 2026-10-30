using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DocProc.Api.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DocProc.Api.Dev;

/// <summary>What <c>POST /dev/token</c> returns; the field names follow the OAuth token response so clients need no special case.</summary>
public sealed record DevTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("user")] DevUser User);

/// <summary>
/// Signs development tokens with an RSA key that lives in this process, or in the PEM file named by
/// <see cref="DevAuthOptions.SigningKeyPath"/> (docs/09 "Running without Docker"). The claims are the ones the
/// Keycloak realm emits and <see cref="TenantResolutionMiddleware"/> and <see cref="Policies"/> read:
/// <c>sub</c>, <c>preferred_username</c>, <c>email</c>, <c>roles</c> and <c>tenant_id</c>.
/// </summary>
public sealed class DevTokenIssuer
{
    public const string KeyId = "docproc-dev";

    private readonly DevAuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public DevTokenIssuer(DevAuthOptions options, OidcOptions oidc, TimeProvider clock, ILogger<DevTokenIssuer> logger)
    {
        _options = options;
        _clock = clock;
        Audience = oidc.Audience;
        SigningKey = LoadOrCreateKey(options.SigningKeyPath, logger);
    }

    public string Audience { get; }

    /// <summary>The public half is all the bearer handler needs; the private half never leaves this class.</summary>
    public RsaSecurityKey SigningKey { get; }

    public DevTokenResponse Issue(DevUser user)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var expires = now.Add(_options.TokenLifetime);
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = user.Subject,
            ["preferred_username"] = user.Username,
            ["email"] = user.Email,
            ["email_verified"] = true,
            ["name"] = user.DisplayName,
            ["given_name"] = user.FirstName,
            ["family_name"] = user.LastName,
            ["roles"] = user.Roles.ToArray(),
        };
        if (user.TenantId is { } tenantId)
        {
            claims["tenant_id"] = tenantId.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = DevAuthOptions.Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(),
        };

        var token = _handler.CreateToken(descriptor);
        return new DevTokenResponse(token, "Bearer", (int)_options.TokenLifetime.TotalSeconds, user);
    }

    private static RsaSecurityKey LoadOrCreateKey(string? path, ILogger logger)
    {
        var rsa = RSA.Create(2048);
        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogInformation("Development signing key generated in memory; tokens stop working when the API restarts.");
            return new RsaSecurityKey(rsa) { KeyId = KeyId };
        }

        var fullPath = Path.GetFullPath(path);
        var loaded = File.Exists(fullPath);
        if (loaded)
        {
            rsa.ImportFromPem(File.ReadAllText(fullPath));
        }
        else
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, rsa.ExportRSAPrivateKeyPem() + Environment.NewLine);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Development signing key {Action} {Path}; tokens survive restarts.", loaded ? "loaded from" : "generated and written to", fullPath);
        }

        return new RsaSecurityKey(rsa) { KeyId = KeyId };
    }
}
