using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>The values the choice-style settings can take.</summary>
public static class MailChoices
{
    /// <summary>The decision is left to the level below: each group (for a site setting) or each restaurant (for a group setting).</summary>
    public const string Each = "each";
    public const string On = "on", Off = "off";
    public const string Live = "live", Test = "test";
    public const string Mail = "mail", Stamp = "stamp";
    public const string Site = "site", Group = "group", Restaurant = "restaurant";
}

/// <summary>
/// What applies to one restaurant once the site's, its investment group's and its own settings are put together, and
/// who decided each: "site" (the System Administrator, for every group), "group" (set for all the group's
/// restaurants) or "restaurant" (the restaurant's own choice).
/// </summary>
/// <param name="MailOn">Cards can be printed and mailed for this restaurant.</param>
/// <param name="MailLive">Cards are really printed, mailed and billed. False: test orders, a proof only.</param>
/// <param name="MailUsual">"Mailed for you" is chosen to begin with on the print screens.</param>
/// <param name="Signature">The name that signs cards entered with the shared team login.</param>
public record RestaurantRules(bool MailOn, bool MailLive, bool MailUsual, string? Signature, string MailOnSetBy, string MailLiveSetBy, string MailUsualSetBy, string SignatureSetBy);

/// <summary>
/// Settings come in levels. A setting made higher up takes precedence, and every such setting says so in plain
/// words: "X for all restaurants", or "Each restaurant decides".
///
/// - Real or test mailing: the System Administrator can fix it for the whole site, or leave it to each investment
///   group; a group (its administrators, or the System Administrator for it) can fix it for all its restaurants, or
///   leave it to each restaurant.
/// - Whether cards can be mailed at all, how cards usually go out, and the name that signs for the team login: set
///   for all of a group's restaurants, or left to each restaurant.
/// - For each of those, the System Administrator either decides it for the group (which takes precedence) or leaves
///   it to the group's own administrators (GroupBilling.GroupDecides). Below them, a restaurant's owners make its
///   own choices where owners were left that permission.
/// </summary>
public static class GroupPolicy
{
    /// <summary>A group's settings at one moment, for the audit log's before and after.</summary>
    public record GroupSettingsSnapshot(string Mail, string MailMode, string UsualWay, string? SignatureForAll, bool? RequirePayMethod, string GroupDecides);

    public static string Switch(GroupBilling? b) => b is null ? MailChoices.Off : b.MailPerRestaurant ? MailChoices.Each : b.MailEnabled ? MailChoices.On : MailChoices.Off;
    public static string SiteMode(BillingSettings site) => site.MailMode is MailChoices.Live or MailChoices.Test ? site.MailMode : MailChoices.Each;
    public static string Mode(GroupBilling? b) => b?.MailMode is MailChoices.Live or MailChoices.Each ? b.MailMode : MailChoices.Test;
    public static string Usual(GroupBilling? b) => b?.UsualWay is MailChoices.Mail or MailChoices.Stamp ? b.UsualWay : MailChoices.Each;
    public static string? SignatureForAll(GroupBilling? b) => string.IsNullOrWhiteSpace(b?.SignatureForAll) ? null : b.SignatureForAll.Trim();

    /// <summary>
    /// Whether the group's own administrators decide this setting. Otherwise it is the System Administrator's answer
    /// for the group, which the group cannot change.
    /// </summary>
    public static bool Decides(GroupBilling? b, GroupChoice choice) => (b?.GroupDecides ?? GroupChoice.Default).HasFlag(choice);

    private static readonly (GroupChoice Choice, string Name)[] Names =
        [(GroupChoice.Mail, "mail"), (GroupChoice.Mode, "mode"), (GroupChoice.Usual, "usual"), (GroupChoice.Signature, "signature")];

    /// <summary>The settings the group's administrators decide, by name: mail, mode, usual, signature.</summary>
    public static string[] DecidedByGroup(GroupBilling? b) => Names.Where(n => Decides(b, n.Choice)).Select(n => n.Name).ToArray();

    public static GroupChoice ParseDecides(IEnumerable<string> names) =>
        Names.Where(n => names.Contains(n.Name, StringComparer.OrdinalIgnoreCase)).Aggregate(GroupChoice.None, (all, n) => all | n.Choice);

    public static GroupSettingsSnapshot Snapshot(GroupBilling b) =>
        new(Switch(b), Mode(b), Usual(b), SignatureForAll(b), b.RequirePayMethod, string.Join(", ", DecidedByGroup(b)));

    /// <summary>The name that signs cards entered with the team login at this restaurant: the group's for all restaurants, else the restaurant's own.</summary>
    public static string? Signature(GroupBilling? b, Restaurant? r) =>
        SignatureForAll(b) ?? (string.IsNullOrWhiteSpace(r?.SignatureName) ? null : r.SignatureName.Trim());

    public static RestaurantRules For(BillingSettings site, GroupBilling? b, Restaurant r)
    {
        var mail = Switch(b);
        var siteMode = SiteMode(site);
        var mode = Mode(b);
        var usual = Usual(b);
        return new RestaurantRules(
            mail == MailChoices.On || (mail == MailChoices.Each && r.MailOn),
            siteMode != MailChoices.Each ? siteMode == MailChoices.Live : mode != MailChoices.Each ? mode == MailChoices.Live : r.MailLive,
            usual == MailChoices.Mail || (usual == MailChoices.Each && r.MailByDefault),
            Signature(b, r),
            mail == MailChoices.Each ? MailChoices.Restaurant : MailChoices.Group,
            siteMode != MailChoices.Each ? MailChoices.Site : mode != MailChoices.Each ? MailChoices.Group : MailChoices.Restaurant,
            usual == MailChoices.Each ? MailChoices.Restaurant : MailChoices.Group,
            SignatureForAll(b) is null ? MailChoices.Restaurant : MailChoices.Group);
    }
}
