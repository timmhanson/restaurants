using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// The start-up check of the database: indexes out of step with their tables are rebuilt (nothing is lost, an index
/// is made from its table), and damage the site cannot repair is reported to the System Administrator instead.
/// </summary>
[Collection(ApiCollection.Name)]
public class DatabaseCareTests(ApiFactory factory)
{
    private static void Run(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Indexes_out_of_step_with_their_table_are_rebuilt_and_a_sound_database_is_left_alone()
    {
        await using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        c.CreateCollation("shifting", string.CompareOrdinal);
        Run(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT COLLATE shifting); CREATE INDEX ix ON t(name);");
        for (var i = 0; i < 40; i++) Run(c, $"INSERT INTO t(name) VALUES ('n{i:000}');");

        var sound = await DatabaseCare.CheckAndRepairAsync(c);
        Assert.True(sound.Sound);
        Assert.False(sound.Rebuilt);
        Assert.Empty(sound.Found);

        // The index was built in one order and is now read in another, so its rows can no longer be found in it:
        // the same state as an index that missed a row while two copies of the site were writing.
        c.CreateCollation("shifting", (a, b) => string.CompareOrdinal(b, a));
        var damaged = await DatabaseCare.CheckAsync(c);
        Assert.NotEmpty(damaged);
        Assert.All(damaged, line => Assert.Contains("missing from index ix", line));

        var repaired = await DatabaseCare.CheckAndRepairAsync(c);
        Assert.True(repaired.Rebuilt);
        Assert.True(repaired.Sound);
        Assert.NotEmpty(repaired.Found);
        Assert.Empty(repaired.Remaining);
        Assert.Empty(await DatabaseCare.CheckAsync(c));
        await using var count = c.CreateCommand();
        count.CommandText = "SELECT count(*) FROM t;";
        Assert.Equal(40L, await count.ExecuteScalarAsync());

        // Only a disagreement between an index and its table counts as repairable. Anything else is left for a person.
        Assert.True(DatabaseCare.IsIndexOnly("row 899 missing from index IX_InteractionLogs_RestaurantId_At"));
        Assert.True(DatabaseCare.IsIndexOnly("wrong # of entries in index IX_InteractionLogs_RestaurantId_At"));
        Assert.False(DatabaseCare.IsIndexOnly("*** in database main ***\nPage 12: btreeInitPage() returns error code 11"));
        Assert.False(DatabaseCare.IsIndexOnly("database disk image is malformed"));
    }

    [Fact]
    public async Task The_site_checks_its_database_at_start_and_tells_the_System_Administrator_about_damage_it_cannot_repair()
    {
        var health = factory.Services.GetRequiredService<DatabaseHealth>();
        Assert.NotNull(health.CheckedAt);
        Assert.Null(health.Problem);

        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        Assert.DoesNotContain((await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention, a => a.Title.Contains("database"));
        try
        {
            health.Problem = "Page 12: btreeInitPage() returns error code 11";
            var item = Assert.Single((await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention, a => a.Title == "The database reports damage");
            Assert.Equal("high", item.Severity);
            Assert.Contains("btreeInitPage", item.Detail);
        }
        finally
        {
            health.Problem = null;
        }
    }
}
