using System.Globalization;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// The figures behind the AI assistant's graphs, for a group's own requests or (for the System Administrator) every
/// group's: how much it is used, by day, level, person and restaurant; which faces the assistant picks; and how often
/// the person kept its face and its reward or changed them, and to what.
/// </summary>
public static class AiCharts
{
    /// <param name="forSite">Include what each request cost the site; left at zero for a group.</param>
    public static AiChartsDto Build(IReadOnlyCollection<AiRequest> rows, IReadOnlyDictionary<int, string> restaurants, bool forSite)
    {
        static bool Ok(AiRequest r) => r.Outcome == AiOutcomes.Ok;
        AiUsageBucket Bucket(string key, string label, IEnumerable<AiRequest> of)
        {
            var list = of.ToList();
            return new AiUsageBucket(key, label, list.Count, list.Count(Ok), list.Count(r => !Ok(r)), list.Count(r => Ok(r) && r.CommentId is null),
                list.Where(Ok).Sum(r => r.PriceMills), forSite ? list.Sum(r => r.CostMicros) : 0);
        }
        static string Face(int? level) => AiMoods.All.FirstOrDefault(m => m.Level == level) is { } m ? $"{m.Face} {m.Label}" : "no face";

        var ok = rows.Where(Ok).ToList();
        var saved = ok.Where(r => r.CommentId is not null).ToList();
        // A card saved without a face keeps the assistant's pick as far as this count goes.
        var faceChanged = saved.Where(r => r.Mood is not null && r.FinalMood is not null && r.FinalMood != r.Mood).ToList();
        var rewardChanged = saved.Where(r => !string.Equals(r.Offer ?? "", r.FinalOffer ?? "", StringComparison.OrdinalIgnoreCase)).ToList();
        return new AiChartsDto(
            rows.GroupBy(r => DateOnly.FromDateTime(r.At)).OrderBy(g => g.Key).Select(g => Bucket(g.Key.ToString("yyyy-MM-dd"), g.Key.ToString("MMM d", CultureInfo.InvariantCulture), g)).ToList(),
            AiLevels.All.Select(l => Bucket(l.Id, forSite ? $"{l.Label} ({l.ModelName})" : l.Label, rows.Where(r => r.Level == l.Id))).ToList(),
            rows.GroupBy(r => r.UserName).Select(g => Bucket(g.Key, g.Key, g)).OrderByDescending(b => b.Requests).Take(12).ToList(),
            rows.GroupBy(r => r.RestaurantId).Select(g => Bucket(g.Key.ToString(), restaurants.GetValueOrDefault(g.Key, $"Restaurant {g.Key}"), g)).OrderByDescending(b => b.Requests).Take(12).ToList(),
            AiMoods.All.Select(m => new AiFaceCount(m.Level, m.Face, m.Label, ok.Count(r => r.Mood == m.Level), saved.Count(r => (r.FinalMood ?? r.Mood) == m.Level))).ToList(),
            saved.Count - faceChanged.Count, faceChanged.Count(r => r.FinalMood > r.Mood), faceChanged.Count(r => r.FinalMood < r.Mood),
            saved.Count - rewardChanged.Count, rewardChanged.Count, ok.Count - saved.Count,
            faceChanged.GroupBy(r => (r.Mood, r.FinalMood)).OrderByDescending(g => g.Count()).Select(g => new AiChange(Face(g.Key.Mood), Face(g.Key.FinalMood), g.Count())).ToList(),
            rewardChanged.GroupBy(r => (From: r.Offer ?? "none", To: string.IsNullOrWhiteSpace(r.FinalOffer) ? "none" : r.FinalOffer!)).OrderByDescending(g => g.Count())
                .Select(g => new AiChange(g.Key.From, g.Key.To, g.Count())).Take(20).ToList());
    }
}
