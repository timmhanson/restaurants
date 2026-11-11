using System.Text.Json;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>Writes an audit row for every meaningful action owners and managers take.</summary>
public class AuditService(AppDbContext db, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Task LogAsync(CurrentUser user, string action, string? entityType = null, object? entityId = null, object? details = null, int? restaurantId = null, int? groupId = null)
        => LogRawAsync(user.Id, user.Username, user.Role, groupId ?? user.GroupId, action, entityType, entityId, details, restaurantId);

    public async Task LogRawAsync(int? userId, string userName, UserRole? role, int? groupId, string action, string? entityType = null, object? entityId = null, object? details = null, int? restaurantId = null)
    {
        InteractionContext.Note(http.HttpContext, action, restaurantId, groupId, (userId, userName, role));
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = userName,
            UserRole = role,
            GroupId = groupId,
            RestaurantId = restaurantId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Details = details is null ? null : (details as string ?? JsonSerializer.Serialize(details, JsonOpts)),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync();
    }
}
