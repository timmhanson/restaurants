using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>One permission as it stands in a group: what the site and the group have each said, and what comes of it.</summary>
/// <param name="SiteOwners">yes (all owners can), no (no owner can) or group (the group's administrators decide).</param>
/// <param name="SiteManagers">yes, no or group. Empty for a permission managers never hold.</param>
/// <param name="GroupOwners">The group's own answer for its owners: yes or no. It counts where the site says "group".</param>
/// <param name="GroupManagers">The group's own answer for its managers: yes, no or each (set manager by manager).</param>
/// <param name="OwnersCan">What comes of it for owners.</param>
/// <param name="OwnersSetBy">site or group.</param>
/// <param name="Managers">What comes of it for managers: all, none or each.</param>
/// <param name="ManagersSetBy">site, group, or owners (no manager can, because the owners cannot either).</param>
public record PermissionState(string Name, string Description, bool ForManagers, bool SpendsMoney, string SiteOwners, string? SiteManagers, string GroupOwners, string? GroupManagers,
    bool OwnersCan, string OwnersSetBy, string Managers, string ManagersSetBy);

/// <summary>
/// Who may do what is handed down level by level, and nobody can hand down more than they hold.
///
/// - The System Administrator, for each investment group, says of every permission: all owners can, no owner can, or
///   the group's administrators decide; and the same for managers. What is said here takes precedence.
/// - Investment Group Administrators can do everything in their group. Where the site leaves it to them, they say
///   whether owners can; and for managers: all can, none can, or it is set manager by manager.
/// - Restaurant Owners hold what is left to them. Owners who hold "Choose what each manager may do" set, manager by
///   manager, the permissions left to that level (the group's administrators always can).
/// - Managers hold what all managers were given, plus what was ticked for them. A manager never holds a permission
///   the group's owners do not.
/// - Team logins only enter cards.
/// </summary>
public static class PermissionPolicy
{
    public const string Yes = "yes", No = "no", Group = "group", Each = "each", All = "all", None = "none", Site = "site", Owners = "owners";

    /// <summary>Permissions that belong to owners and are never given to a manager.</summary>
    public const ManagerPermission OwnerOnly = ManagerPermission.ManageStaff | ManagerPermission.SetPermissions | ManagerPermission.EditRestaurants | ManagerPermission.DesignCards;
    public const ManagerPermission ForManagers = ManagerPermission.All & ~OwnerOnly;

    public static IEnumerable<ManagerPermission> Each_() =>
        Enum.GetValues<ManagerPermission>().Where(v => v != ManagerPermission.None && v != ManagerPermission.All);

    /// <summary>What the group's owners hold.</summary>
    public static ManagerPermission OwnersHold(OwnerGroup? g) =>
        g is null ? ManagerPermission.All : ManagerPermission.All & ~(g.SiteOwnerNo | (g.OwnerNo & ~g.SiteOwnerYes));

    /// <summary>What every manager in the group holds, whatever is ticked for them.</summary>
    public static ManagerPermission AllManagers(OwnerGroup? g) =>
        g is null ? ManagerPermission.None : (g.SiteManagerYes | (g.ManagerYes & ~g.SiteManagerNo)) & OwnersHold(g) & ForManagers;

    /// <summary>What no manager in the group can hold.</summary>
    public static ManagerPermission NoManager(OwnerGroup? g) =>
        (g is null ? ManagerPermission.None : g.SiteManagerNo | (g.ManagerNo & ~g.SiteManagerYes)) | ~OwnersHold(g) | OwnerOnly;

    /// <summary>What is set manager by manager: by owners who may, and by the group's administrators.</summary>
    public static ManagerPermission PerManager(OwnerGroup? g) => ForManagers & ~AllManagers(g) & ~NoManager(g);

    /// <summary>What this person holds, in this group.</summary>
    public static ManagerPermission Effective(UserRole role, ManagerPermission own, OwnerGroup? g) => role switch
    {
        UserRole.SuperAdmin or UserRole.GroupAdmin => ManagerPermission.All,
        UserRole.Owner => OwnersHold(g),
        UserRole.Manager => AllManagers(g) | (own & PerManager(g)),
        _ => ManagerPermission.None
    };

    public static bool SpendsMoney(ManagerPermission p) => p == ManagerPermission.BuyPostage;

    public static List<PermissionState> States(OwnerGroup? g)
    {
        var owners = OwnersHold(g);
        var all = AllManagers(g);
        var none = NoManager(g);
        return Each_().Select(p =>
        {
            var forManagers = ForManagers.HasFlag(p);
            var siteOwners = g is null ? Group : g.SiteOwnerYes.HasFlag(p) ? Yes : g.SiteOwnerNo.HasFlag(p) ? No : Group;
            var siteManagers = !forManagers ? null : g is null ? Group : g.SiteManagerYes.HasFlag(p) ? Yes : g.SiteManagerNo.HasFlag(p) ? No : Group;
            var groupOwners = g is not null && g.OwnerNo.HasFlag(p) ? No : Yes;
            var groupManagers = !forManagers ? null : g is null ? Each : g.ManagerYes.HasFlag(p) ? Yes : g.ManagerNo.HasFlag(p) ? No : Each;
            var managers = all.HasFlag(p) ? All : none.HasFlag(p) ? None : Each;
            var managersSetBy = !forManagers ? Site : !owners.HasFlag(p) ? Owners : siteManagers != Group ? Site : Group;
            return new PermissionState(p.ToString(), Describe(p), forManagers, SpendsMoney(p), siteOwners, siteManagers, groupOwners, groupManagers,
                owners.HasFlag(p), siteOwners != Group ? Site : Group, managers, managersSetBy);
        }).ToList();
    }

    public static string Describe(ManagerPermission p) => p switch
    {
        ManagerPermission.ViewComments => "View guest comments for their restaurants",
        ManagerPermission.EditComments => "Edit guest comments after entry",
        ManagerPermission.DeleteComments => "Remove guest comments",
        ManagerPermission.MarkResponded => "Print and mail coupon cards, and mark comments as responded",
        ManagerPermission.ViewAnalytics => "See graphs, analytics and roll-up reports across their restaurants",
        ManagerPermission.ExportComments => "Download comments as CSV",
        ManagerPermission.ViewAudit => "View the audit log for their restaurants",
        ManagerPermission.ManageTeamLogin => "Create and reset the shared team login",
        ManagerPermission.BuyPostage => "Spend money: buy postage and have cards mailed",
        ManagerPermission.SetFollowUpDays => "Set how many days after a card is sent the guest is followed up with",
        ManagerPermission.ManageStaff => "Add, change, reset and suspend managers and team logins, and assign them to restaurants",
        ManagerPermission.SetPermissions => "Choose what each manager may do",
        ManagerPermission.EditRestaurants => "Add restaurants, and change a restaurant's details and coupon card settings",
        ManagerPermission.DesignCards => "Change a restaurant's coupon card design",
        _ => p.ToString()
    };

    public static ManagerPermission Parse(IEnumerable<string>? names)
    {
        var result = ManagerPermission.None;
        foreach (var n in names ?? [])
            if (Enum.TryParse<ManagerPermission>(n, true, out var p) && p != ManagerPermission.All && Enum.IsDefined(p)) result |= p;
        return result;
    }
}
