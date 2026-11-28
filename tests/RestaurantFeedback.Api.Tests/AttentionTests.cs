using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>The Overview's "Needs your attention" list: dismissing an item, and its coming back when it changes.</summary>
[Collection(ApiCollection.Name)]
public class AttentionTests(ApiFactory factory)
{
    [Fact]
    public async Task A_dismissed_item_stays_away_until_it_changes_or_comes_back()
    {
        var errors = new AttentionItem("high", "3 server errors in the last 24 hours", "Something failed for someone.", "/system");
        var more = errors with { Title = "5 server errors in the last 24 hours" };
        var noAdmin = new AttentionItem("high", "North Group has no Investment Group Administrator", "Nobody can manage it.", "/groups");
        var other = noAdmin with { Title = "South Group has no Investment Group Administrator" };

        // The same item whatever its count; a different item for a different group.
        Assert.Equal(Attention.Key(errors), Attention.Key(more));
        Assert.NotEqual(Attention.Fingerprint(errors), Attention.Fingerprint(more));
        Assert.NotEqual(Attention.Key(noAdmin), Attention.Key(other));
        // What each is about, told from where it leads.
        Assert.Equal(("system", "restaurants"), (Attention.Scope(errors), Attention.Scope(noAdmin)));
        Assert.Equal("system", Attention.Scope(new AttentionItem("info", "You are the only System Administrator", "", "/people?role=SuperAdmin")));
        Assert.Equal("restaurants", Attention.Scope(new AttentionItem("medium", "2 people have never signed in", "", "/people?status=never")));
        Assert.Equal("restaurants", Attention.Scope(new AttentionItem("high", "1 mailing bill is overdue ($9.90)", "", "/system-billing")));
        Assert.Equal("system", Attention.Scope(new AttentionItem("info", "Guest addresses are not being checked", "", "/connected-systems")));

        const int user = 987_001;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try
        {
            var first = await Attention.MarkAsync(db, user, [errors, noAdmin]);
            Assert.All(first, a => Assert.False(a.Dismissed));
            db.AttentionDismissals.Add(new AttentionDismissal { UserId = user, Key = first[0].Key, Fingerprint = first[0].Fingerprint, Title = errors.Title });
            await db.SaveChangesAsync();

            // Dismissed for this person while it reads the same; not for anyone else.
            var same = await Attention.MarkAsync(db, user, [errors, noAdmin]);
            Assert.Equal((true, false), (same[0].Dismissed, same[1].Dismissed));
            Assert.NotNull(same[0].DismissedAt);
            Assert.False((await Attention.MarkAsync(db, user + 1, [errors, noAdmin]))[0].Dismissed);

            // It changes: it shows again, and the dismissal is forgotten, so going back to the old wording does not hide it.
            Assert.False((await Attention.MarkAsync(db, user, [more, noAdmin]))[0].Dismissed);
            Assert.False((await Attention.MarkAsync(db, user, [errors, noAdmin]))[0].Dismissed);

            // It goes away and returns: new again.
            db.AttentionDismissals.Add(new AttentionDismissal { UserId = user, Key = first[1].Key, Fingerprint = first[1].Fingerprint, Title = noAdmin.Title });
            await db.SaveChangesAsync();
            Assert.True((await Attention.MarkAsync(db, user, [noAdmin]))[0].Dismissed);
            await Attention.MarkAsync(db, user, []);
            Assert.False((await Attention.MarkAsync(db, user, [noAdmin]))[0].Dismissed);
        }
        finally
        {
            db.AttentionDismissals.RemoveRange(db.AttentionDismissals.Where(d => d.UserId == user || d.UserId == user + 1));
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Only_a_System_Administrator_dismisses_and_brings_back_items_on_the_Overview()
    {
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/system/attention/dismiss", new AttentionDismissRequest("k", "f", "t"))).StatusCode);

        var overview = (await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!;
        Assert.All(overview.Attention, a => Assert.True(a.Key.Length > 0 && a.Fingerprint.Length > 0 && a.Scope is "system" or "restaurants"));
        if (overview.Attention.FirstOrDefault(a => !a.Dismissed) is not { } item) return; // nothing on the list in this run

        Assert.Equal(HttpStatusCode.NoContent, (await site.PostAsJsonAsync("/api/system/attention/dismiss", new AttentionDismissRequest(item.Key, item.Fingerprint, item.Title))).StatusCode);
        var after = (await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention.FirstOrDefault(a => a.Key == item.Key);
        // Still dismissed if it reads the same; if its count moved in between, it is rightly back.
        if (after is not null && after.Fingerprint == item.Fingerprint) Assert.True(after.Dismissed);
        Assert.Equal(HttpStatusCode.NoContent, (await site.PostAsJsonAsync("/api/system/attention/restore", new AttentionRestoreRequest(item.Key))).StatusCode);
        Assert.False((await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention.FirstOrDefault(a => a.Key == item.Key)?.Dismissed ?? false);
    }
}
