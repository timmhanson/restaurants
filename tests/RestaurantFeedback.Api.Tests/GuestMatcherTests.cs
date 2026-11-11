using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

public class GuestMatcherTests
{
    [Theory]
    [InlineData("(815) 555-1234", "8155551234")]
    [InlineData("1-815-555-1234", "8155551234")]
    [InlineData("815.555.1234", "8155551234")]
    [InlineData("555-1234", "5551234")]
    [InlineData("", null)]
    [InlineData("12", null)]
    public void NormalizePhone_strips_formatting(string input, string? expected)
        => Assert.Equal(expected, GuestMatcher.NormalizePhone(input));

    [Fact]
    public void Phone_wins_over_name_when_building_the_key()
    {
        var a = GuestMatcher.BuildGuestKey("Jane", "Doe", "(815) 555-1234", "123 Main St", "60431");
        var b = GuestMatcher.BuildGuestKey("J.", "Doe", "815-555-1234", null, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Name_and_street_match_despite_abbreviations_and_case()
    {
        var a = GuestMatcher.BuildGuestKey("Jane", "Doe", null, "123 North Main Street", null);
        var b = GuestMatcher.BuildGuestKey("JANE", "doe ", null, "123 N. Main St.", null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_guests_get_different_keys()
    {
        var a = GuestMatcher.BuildGuestKey("Jane", "Doe", null, "123 Main St", null);
        var b = GuestMatcher.BuildGuestKey("John", "Doe", null, "123 Main St", null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Name_only_falls_back_to_zip_then_name()
    {
        Assert.Equal("n:jane doe|z:60431", GuestMatcher.BuildGuestKey("Jane", "Doe", null, null, "60431-1234"));
        Assert.Equal("n:jane doe", GuestMatcher.BuildGuestKey("Jane", "Doe", null, null, null));
    }
}
