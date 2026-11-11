using System.Text;
using System.Text.RegularExpressions;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// Normalises guest identity fields so the same person can be recognised across comments
/// even when the card was transcribed slightly differently.
/// </summary>
public static partial class GuestMatcher
{
    [GeneratedRegex(@"[^a-z0-9 ]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\D")] private static partial Regex NonDigit();

    private static readonly Dictionary<string, string> StreetAbbrev = new()
    {
        ["street"] = "st", ["avenue"] = "ave", ["road"] = "rd", ["drive"] = "dr", ["lane"] = "ln",
        ["boulevard"] = "blvd", ["court"] = "ct", ["circle"] = "cir", ["place"] = "pl", ["terrace"] = "ter",
        ["parkway"] = "pkwy", ["highway"] = "hwy", ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w",
        ["apartment"] = "apt", ["suite"] = "ste", ["unit"] = "unit"
    };

    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = NonDigit().Replace(phone, "");
        if (digits.Length > 10 && digits.StartsWith('1')) digits = digits[1..];
        return digits.Length >= 7 ? digits : null;
    }

    public static string NormalizeName(string? first, string? last)
        => Collapse($"{first} {last}");

    public static string? NormalizeAddress(string? street)
    {
        if (string.IsNullOrWhiteSpace(street)) return null;
        var words = Collapse(street).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => StreetAbbrev.TryGetValue(w, out var abbr) ? abbr : w);
        var result = string.Join(' ', words);
        return result.Length == 0 ? null : result;
    }

    public static string? NormalizeZip(string? zip)
    {
        if (string.IsNullOrWhiteSpace(zip)) return null;
        var digits = NonDigit().Replace(zip, "");
        return digits.Length >= 5 ? digits[..5] : null;
    }

    /// <summary>
    /// The identity key for a guest. Phone number wins when present, since it is the most
    /// reliable field on the card. Otherwise name + street (or name + zip, or name alone).
    /// </summary>
    public static string BuildGuestKey(string? first, string? last, string? phone, string? street, string? zip)
    {
        var p = NormalizePhone(phone);
        if (p is not null) return "p:" + p;
        var name = NormalizeName(first, last);
        var addr = NormalizeAddress(street);
        if (addr is not null) return $"n:{name}|a:{addr}";
        var z = NormalizeZip(zip);
        if (z is not null) return $"n:{name}|z:{z}";
        return "n:" + name;
    }

    private static string Collapse(string s)
    {
        var lowered = NonAlnum().Replace(s.ToLowerInvariant().Normalize(NormalizationForm.FormD), "");
        return Spaces().Replace(lowered, " ").Trim();
    }
}
