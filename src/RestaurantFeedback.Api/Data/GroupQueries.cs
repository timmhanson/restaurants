using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Data;

public static class GroupQueries
{
    /// <summary>
    /// True when the group has an active investment group administrator (optionally other than one user). Such a group can run
    /// restaurants with no specific owner, and administrators, not owners, decide who owns what.
    /// </summary>
    public static Task<bool> GroupHasAdminAsync(this AppDbContext db, int? groupId, int? exceptUserId = null) =>
        groupId is null
            ? Task.FromResult(false)
            : db.Users.AnyAsync(u => u.GroupId == groupId && u.Role == UserRole.GroupAdmin && u.IsActive && u.Id != exceptUserId);
}
