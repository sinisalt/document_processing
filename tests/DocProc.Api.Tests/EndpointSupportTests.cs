using DocProc.Api.Endpoints;
using DocProc.Infrastructure.Persistence.Entities;

namespace DocProc.Api.Tests;

public sealed class EndpointSupportTests
{
    [Theory]
    [InlineData("TenantAdmin", TenantRole.TenantAdmin)]
    [InlineData("tenantadmin", TenantRole.TenantAdmin)]
    [InlineData("REVIEWER", TenantRole.Reviewer)]
    [InlineData(" viewer ", TenantRole.Viewer)]
    public void TryParseEnum_accepts_member_names_in_any_case(string value, TenantRole expected)
    {
        EndpointSupport.TryParseEnum<TenantRole>(value, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    [InlineData("Owner")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void TryParseEnum_refuses_numbers_unknown_names_and_blanks(string? value)
    {
        EndpointSupport.TryParseEnum<TenantRole>(value, out var parsed).ShouldBeFalse();
        parsed.ShouldBe(default);
        Should.Throw<ArgumentException>(() => EndpointSupport.ParseEnum<TenantRole>(value));
    }

    [Theory]
    [InlineData("auth0|abc", true)]
    [InlineData("oidc:user@idp.example", true)]
    [InlineData("auth0/abc", false)]
    [InlineData("user id", false)]
    [InlineData("user\tid", false)]
    [InlineData("user\u0001id", false)]
    public void Member_user_ids_must_be_routable(string userId, bool routable) =>
        MemberEndpoints.IsRoutableUserId(userId).ShouldBe(routable);
}
