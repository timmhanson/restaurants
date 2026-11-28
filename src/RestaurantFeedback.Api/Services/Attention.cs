using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// The Overview's "Needs your attention" list, as one System Administrator sees it: what each item is about (the
/// site itself, or the investment groups and their restaurants) and which ones they have dismissed.
/// </summary>
public static class Attention
{
    public const string System = "system", Restaurants = "restaurants";

    /// <summary>
    /// Which item this is, whatever numbers it holds at the moment: where it leads and its title with the figures
    /// taken out, so "3 server errors" and "5 server errors" are the same item, and two groups' items are not.
    /// </summary>
    public static string Key(AttentionItem a) => $"{a.Link}|{Regex.Replace(a.Title, @"\d[\d,.:$]*", "#")}";

    /// <summary>What the item says right now. A dismissal holds only while this stays the same.</summary>
    public static string Fingerprint(AttentionItem a) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{a.Severity}|{a.Title}|{a.Detail}")))[..24];

    /// <summary>
    /// The site itself (backups, errors, keys, email, security, System Administrators), or the investment groups:
    /// their restaurants, people, cards and bills. Told from where the item leads.
    /// </summary>
    public static string Scope(AttentionItem a)
    {
        var link = a.Link ?? "";
        if (link.StartsWith("/people?role=SuperAdmin", StringComparison.OrdinalIgnoreCase)) return System;
        return link.StartsWith("/groups") || link.StartsWith("/reports") || link.StartsWith("/all-restaurants") || link.StartsWith("/people") || link.StartsWith("/system-billing")
            ? Restaurants : System;
    }

    /// <summary>
    /// Marks each item with its key, what it is about and whether this person has dismissed it. A dismissal whose
    /// item has changed or is no longer on the list is forgotten, so the item shows again when it changes or returns.
    /// </summary>
    public static async Task<List<AttentionItem>> MarkAsync(AppDbContext db, int userId, List<AttentionItem> items)
    {
        var mine = await db.AttentionDismissals.Where(d => d.UserId == userId).ToListAsync();
        var marked = items.Select(a =>
        {
            var (key, fingerprint) = (Key(a), Fingerprint(a));
            var dismissal = mine.FirstOrDefault(d => d.Key == key && d.Fingerprint == fingerprint);
            return a with { Key = key, Fingerprint = fingerprint, Scope = Scope(a), Dismissed = dismissal is not null, DismissedAt = dismissal is null ? null : DateTime.SpecifyKind(dismissal.DismissedAt, DateTimeKind.Utc) };
        }).ToList();
        var gone = mine.Where(d => !marked.Any(a => a.Key == d.Key && a.Fingerprint == d.Fingerprint)).ToList();
        if (gone.Count > 0)
        {
            db.AttentionDismissals.RemoveRange(gone);
            await db.SaveChangesAsync();
        }
        return marked;
    }
}
