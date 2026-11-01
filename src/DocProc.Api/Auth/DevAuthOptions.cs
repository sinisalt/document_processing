namespace DocProc.Api.Auth;

/// <summary>
/// The dependency-free development identity provider (docs/09 "Running without Docker"): when enabled outside
/// production, the API signs its own tokens for the users of the local Keycloak realm and the bearer handler trusts
/// that key instead of an OIDC authority. Never enabled in production; the host refuses to start.
/// </summary>
public sealed class DevAuthOptions
{
    public const string SectionName = "Authentication:Dev";

    /// <summary>The <c>iss</c> of every development token; the bearer handler accepts no other issuer while enabled.</summary>
    public const string Issuer = "docproc-dev";

    public bool Enabled { get; set; }

    /// <summary>
    /// PEM file holding the RSA signing key. Unset: a fresh key per process, so tokens die with it. Set: the key is
    /// loaded, or generated and written there on first use, so tokens survive restarts.
    /// </summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>Lifetime of an issued token; a working day by default, long enough for a demo or a Playwright run.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);
}
