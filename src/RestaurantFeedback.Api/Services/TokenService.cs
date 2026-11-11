using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public class JwtOptions
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "RestaurantFeedback";
    public string Audience { get; set; } = "RestaurantFeedback";
    public int LifetimeHours { get; set; } = 12;
}

public class TokenService(JwtOptions options)
{
    public (string token, DateTime expires) Create(AppUser user, IEnumerable<int> restaurantIds)
    {
        var expires = DateTime.UtcNow.AddHours(options.LifetimeHours);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("name", user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("gid", user.GroupId?.ToString() ?? ""),
            new("pwc", user.MustChangePassword ? "1" : "0"),
            new("perm", ((int)user.Permissions).ToString()),
            new("rids", string.Join(',', restaurantIds)),
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
