using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Encore.Api.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "encore";
    public string Audience { get; set; } = "encore";

    /// <summary>HMAC key, at least 32 bytes. Comes from env/secrets, never from source control in a real system.</summary>
    public string SigningKey { get; set; } = "";

    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed record TokenRequest(
    [property: Required, RegularExpression("^[a-z0-9_-]{3,32}$", ErrorMessage = "3-32 chars: a-z, 0-9, _ or -.")]
    string Username);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

public static class AuthEndpoints
{
    /// <summary>
    /// Demo login: any valid username gets a signed JWT. Stands in for a real identity
    /// provider (Entra ID, Auth0, Cognito); everything downstream only trusts the token.
    /// </summary>
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/auth/token", (TokenRequest request, IOptions<JwtOptions> options, TimeProvider clock) =>
            {
                var jwt = options.Value;
                var expires = clock.GetUtcNow().AddHours(8);
                var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, request.Username)]),
                    Issuer = jwt.Issuer,
                    Audience = jwt.Audience,
                    Expires = expires.UtcDateTime,
                    SigningCredentials = new SigningCredentials(jwt.Key, SecurityAlgorithms.HmacSha256),
                });
                return TypedResults.Ok(new TokenResponse(token, expires));
            })
            .AllowAnonymous()
            .WithTags("Auth")
            .WithSummary("Issue a demo access token");

        return group;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? throw new InvalidOperationException("Authenticated user without a 'sub' claim.");
}
