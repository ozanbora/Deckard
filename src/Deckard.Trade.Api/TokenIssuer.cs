using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Deckard.Trade.Api;

/// <summary>Signs the API's own access tokens once a player has signed in; they carry the user id, BattleTag and session (<see cref="Sessions"/>).</summary>
public sealed class TokenIssuer(SymmetricSecurityKey key)
{
    public const string Issuer = "deckard-trade";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public string Issue(UserRow user, Guid session, DateTimeOffset expires) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: Issuer,
        audience: Issuer,
        claims: [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.BattleTag), new Claim(Sessions.Claim, session.ToString())],
        expires: expires.UtcDateTime,
        signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));

    public static TokenValidationParameters ValidationParameters(SymmetricSecurityKey key) => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Issuer,
        IssuerSigningKey = key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromMinutes(1),
    };
}
