using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ExecPlan.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ExecPlan.Infrastructure.Auth;

/// <summary>HS256 access-token factory. Reads <c>Jwt:Issuer</c>/<c>Jwt:Audience</c>/<c>Jwt:SigningKey</c>/
/// <c>Jwt:AccessTokenMinutes</c> from configuration. Claims: sub=UserId, role=Role, name=FullName, jti=unique.</summary>
public sealed class JwtTokenFactory : IJwtTokenFactory
{
    private readonly IConfiguration _cfg;

    public JwtTokenFactory(IConfiguration cfg) => _cfg = cfg;

    public (string token, DateTime expiresUtc) Create(AppUserPrincipal user)
    {
        var signingKey = _cfg["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Jwt:SigningKey is not configured.");
        }

        var issuer = _cfg["Jwt:Issuer"];
        var audience = _cfg["Jwt:Audience"];
        var minutes = double.TryParse(_cfg["Jwt:AccessTokenMinutes"], out var parsedMinutes) ? parsedMinutes : 30d;

        var now = DateTime.UtcNow;
        var expiresUtc = now.AddMinutes(minutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(JwtRegisteredClaimNames.Name, user.FullName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(issuer, audience, claims, notBefore: now, expires: expiresUtc, signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresUtc);
    }
}
