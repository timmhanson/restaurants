using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

[ApiController]
[Route("api/comments")]
[Authorize]
public class CommentsController(AppDbContext db, AuditService audit, IGroupSettingsStore settingsStore, CurrentUserProvider users) : ControllerBase
{
    /// <summary>Comments for one restaurant, or every restaurant the user can see when restaurantId is omitted.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<CommentSummary>>> List(
        [FromQuery] int? restaurantId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] bool? responded,
        [FromQuery] string? reason, [FromQuery] string? search, [FromQuery] bool repeatOnly = false, [FromQuery] string? status = null,
        [FromQuery] string sort = "visitDate", [FromQuery] string dir = "desc", [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments)) return Forbid();
        var ids = ScopedRestaurantIds(me, restaurantId);
        if (ids is null) return Forbid();

        var q = db.Comments.Include(c => c.Restaurant).Where(c => ids.Contains(c.RestaurantId));
        if (from is not null) q = q.Where(c => c.VisitDate >= from);
        if (to is not null) q = q.Where(c => c.VisitDate <= to);
        if (responded is not null) q = q.Where(c => c.Responded == responded);
        // Follow-up queue: needs-reply = nobody has tried yet, attempted = tried but not replied, replied = done.
        var attempted = db.CommentActivities.Where(a => a.Kind == ActivityKind.Attempt).Select(a => a.CommentId);
        q = status switch
        {
            "needs-reply" => q.Where(c => !c.Responded && !attempted.Contains(c.Id)),
            "attempted" => q.Where(c => !c.Responded && attempted.Contains(c.Id)),
            "waiting" => q.Where(c => !c.Responded),
            "replied" => q.Where(c => c.Responded),
            _ => q
        };
        if (!string.IsNullOrWhiteSpace(reason)) q = q.Where(c => c.Reason == reason);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            var digits = GuestMatcher.NormalizePhone(search);
            q = q.Where(c => (c.GuestFirstName + " " + c.GuestLastName).ToLower().Contains(s)
                             || (c.StreetAddress ?? "").ToLower().Contains(s)
                             || (c.City ?? "").ToLower().Contains(s)
                             || (c.ReasonDetails ?? "").ToLower().Contains(s)
                             || (c.CouponOffer ?? "").ToLower().Contains(s)
                             || (digits != null && c.PhoneNormalized != null && c.PhoneNormalized.Contains(digits)));
        }

        // Guest counts are computed across the whole group so a guest sending cards to two
        // restaurants in the same investment group is still spotted.
        var groupCounts = db.Comments.Where(c => c.Restaurant.GroupId == me.GroupId)
            .GroupBy(c => c.GuestKey).Select(g => new { GuestKey = g.Key, Count = g.Count() });
        if (repeatOnly)
        {
            var repeatKeys = groupCounts.Where(g => g.Count >= 2).Select(g => g.GuestKey);
            q = q.Where(c => repeatKeys.Contains(c.GuestKey));
        }

        var total = await q.CountAsync();
        var desc = dir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        q = sort switch
        {
            "createdAt" => desc ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
            "guest" => desc ? q.OrderByDescending(c => c.GuestLastName).ThenByDescending(c => c.GuestFirstName) : q.OrderBy(c => c.GuestLastName).ThenBy(c => c.GuestFirstName),
            "reason" => desc ? q.OrderByDescending(c => c.Reason) : q.OrderBy(c => c.Reason),
            "lastActivity" => desc ? q.OrderByDescending(c => c.LastActivityAt ?? c.CreatedAt) : q.OrderBy(c => c.LastActivityAt ?? c.CreatedAt),
            _ => desc ? q.OrderByDescending(c => c.VisitDate).ThenByDescending(c => c.Id) : q.OrderBy(c => c.VisitDate).ThenBy(c => c.Id)
        };
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 500);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var keys = items.Select(c => c.GuestKey).Distinct().ToList();
        var counts = await groupCounts.Where(g => keys.Contains(g.GuestKey)).ToDictionaryAsync(g => g.GuestKey, g => g.Count);
        return new PagedResult<CommentSummary>(await Summaries(items, c => counts.GetValueOrDefault(c.GuestKey, 1)), total, page, pageSize);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CommentSummary>> Get(int id)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments)) return Forbid();
        var c = await db.Comments.Include(x => x.Restaurant).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId)) return NotFound();
        var count = await db.Comments.CountAsync(x => x.GuestKey == c.GuestKey && x.Restaurant.GroupId == me.GroupId);
        return (await Summaries([c], _ => count))[0];
    }

    /// <summary>The follow-up history of a comment: notes, attempts, replies and reopenings, oldest first.</summary>
    [HttpGet("{id:int}/activity")]
    public async Task<ActionResult<List<ActivityDto>>> Activity(int id)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments)) return Forbid();
        var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId)) return NotFound();
        return await ActivityOf(id);
    }

    /// <summary>
    /// Records a note, a contact attempt, a confirmed reply, or reopens a comment. Notes need ViewComments; the
    /// others change the reply status and need MarkResponded.
    /// </summary>
    [HttpPost("{id:int}/followup")]
    public async Task<ActionResult<FollowUpResponse>> FollowUp(int id, FollowUpRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!Enum.TryParse<ActivityKind>(req.Kind, true, out var kind) || !Enum.IsDefined(kind)) return BadRequest(new { message = "Unknown follow-up type." });
        if (!me.Has(kind == ActivityKind.Note ? ManagerPermission.ViewComments : ManagerPermission.MarkResponded)) return Forbid();
        var c = await db.Comments.Include(x => x.Restaurant).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId)) return NotFound();

        var text = string.IsNullOrWhiteSpace(req.Text) ? null : req.Text.Trim();
        var method = string.IsNullOrWhiteSpace(req.Method) ? null : req.Method.Trim();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var on = req.ContactedOn;
        if (kind is ActivityKind.Attempt or ActivityKind.Replied)
        {
            if (method is null) return BadRequest(new { message = "Choose how you contacted the guest." });
            on ??= today;
            // Allow a day of slack for time zones west of UTC.
            if (on > today.AddDays(1)) return BadRequest(new { message = "The contact date cannot be in the future." });
            if (on < c.VisitDate) return BadRequest(new { message = $"The contact date cannot be before the visit on {c.VisitDate:MMM d, yyyy}." });
        }
        else on = null;

        switch (kind)
        {
            case ActivityKind.Note:
                if (text is null) return BadRequest(new { message = "Write a note first." });
                break;
            case ActivityKind.Attempt:
                if (c.Responded) return BadRequest(new { message = "This guest has already been replied to. Add a note instead." });
                break;
            case ActivityKind.Replied:
                if (c.Responded) return BadRequest(new { message = $"{c.RespondedByName} already confirmed a reply on {c.RepliedOn:MMM d}. Reopen it first if it needs another reply." });
                if (!req.Confirmed) return BadRequest(new { message = "Tick the box to confirm the reply was actually sent or made." });
                c.Responded = true;
                c.RespondedAt = DateTime.UtcNow;
                c.RespondedByUserId = me.Id;
                c.RespondedByName = me.DisplayName;
                c.ReplyMethod = method;
                c.RepliedOn = on;
                break;
            case ActivityKind.Reopened:
                if (!c.Responded) return BadRequest(new { message = "This comment is already waiting for a reply." });
                if (text is null) return BadRequest(new { message = "Say why it needs another reply, so the other managers know." });
                c.Responded = false;
                c.RespondedAt = null; c.RespondedByUserId = null; c.RespondedByName = null; c.ReplyMethod = null; c.RepliedOn = null;
                break;
        }
        var entry = new CommentActivity
        {
            CommentId = c.Id, Kind = kind, Method = kind is ActivityKind.Attempt or ActivityKind.Replied ? method : null, ContactedOn = on, Text = text,
            CreatedByUserId = me.Id, CreatedByName = me.DisplayName, CreatedByRole = me.Role
        };
        db.CommentActivities.Add(entry);
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, kind switch
        {
            ActivityKind.Note => "CommentNoteAdded",
            ActivityKind.Attempt => "CommentContactAttempted",
            ActivityKind.Replied => "CommentMarkedResponded",
            _ => "CommentMarkedUnresponded"
        }, "Comment", c.Id, new { entry.Method, entry.ContactedOn, entry.Text }, c.RestaurantId);

        var count = await db.Comments.CountAsync(x => x.GuestKey == c.GuestKey && x.Restaurant.GroupId == me.GroupId, ct);
        return new FollowUpResponse((await Summaries([c], _ => count))[0], await ActivityOf(c.Id));
    }

    /// <summary>Every card on file for a guest across the restaurants the user can see.</summary>
    [HttpGet("guest/{guestKey}")]
    public async Task<ActionResult<List<CommentSummary>>> GuestHistory(string guestKey)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments)) return Forbid();
        var list = await db.Comments.Include(c => c.Restaurant)
            .Where(c => c.GuestKey == guestKey && me.RestaurantIds.Contains(c.RestaurantId))
            .OrderByDescending(c => c.VisitDate).ToListAsync();
        return list.Select(c => CommentSummary.From(c, list.Count)).ToList();
    }

    /// <summary>Dry-run duplicate / repeat-guest detection. Team members may call this too.</summary>
    [HttpPost("check")]
    public async Task<ActionResult<DuplicateCheckResult>> Check(CommentRequest req, [FromQuery] int? excludeId, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.CanAccessRestaurant(req.RestaurantId)) return Forbid();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        // excludeId lets the edit form re-check a card without matching itself.
        return await Detect(me, req, settings, excludeId, ct);
    }

    [HttpPost]
    public async Task<ActionResult<CommentSummary>> Create(CommentRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.CanAccessRestaurant(req.RestaurantId)) return Forbid();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        var validation = Validate(req, settings);
        if (validation is not null) return BadRequest(new { message = validation });

        var check = await Detect(me, req, settings, excludeId: null, ct);
        if ((check.ExactDuplicate || check.LikelyDuplicate) && !req.Force)
            return Conflict(check);

        var c = new GuestComment { RestaurantId = req.RestaurantId, CreatedByUserId = me.Id, CreatedByName = me.DisplayName };
        Apply(c, req, settings);
        db.Comments.Add(c);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "CommentCreated", "Comment", c.Id,
            new { Guest = $"{c.GuestFirstName} {c.GuestLastName}", c.VisitDate, c.Reason, c.CouponOffer, Forced = req.Force, check.PriorCount }, c.RestaurantId);
        c.Restaurant = await db.Restaurants.FindAsync([c.RestaurantId], ct) ?? new Restaurant();
        return CreatedAtAction(nameof(Get), new { id = c.Id }, CommentSummary.From(c, check.PriorCount + 1));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CommentSummary>> Update(int id, CommentRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.EditComments)) return Forbid();
        var c = await db.Comments.Include(x => x.Restaurant).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId) || !me.CanAccessRestaurant(req.RestaurantId)) return NotFound();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        var validation = Validate(req, settings);
        if (validation is not null) return BadRequest(new { message = validation });

        var check = await Detect(me, req, settings, excludeId: id, ct);
        if (check.ExactDuplicate && !req.Force) return Conflict(check);

        var before = CommentSummary.From(c, 0);
        Apply(c, req, settings);
        c.UpdatedAt = DateTime.UtcNow; c.UpdatedByUserId = me.Id;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CommentUpdated", "Comment", c.Id, new { before = Trim(before), after = Trim(CommentSummary.From(c, 0)) }, c.RestaurantId);
        return CommentSummary.From(c, check.PriorCount + 1);
    }

    [HttpPost("{id:int}/responded")]
    public async Task<ActionResult<CommentSummary>> MarkResponded(int id, RespondRequest req)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await db.Comments.Include(x => x.Restaurant).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId)) return NotFound();
        // Quick toggle kept for older clients; the app uses /followup, which records how and when the guest was replied to.
        if (c.Responded != req.Responded)
        {
            c.Responded = req.Responded;
            c.RespondedAt = req.Responded ? DateTime.UtcNow : null;
            c.RespondedByUserId = req.Responded ? me.Id : null;
            c.RespondedByName = req.Responded ? me.DisplayName : null;
            c.ReplyMethod = null;
            c.RepliedOn = req.Responded ? DateOnly.FromDateTime(DateTime.UtcNow) : null;
            var entry = new CommentActivity
            {
                CommentId = c.Id, Kind = req.Responded ? ActivityKind.Replied : ActivityKind.Reopened, ContactedOn = c.RepliedOn,
                CreatedByUserId = me.Id, CreatedByName = me.DisplayName, CreatedByRole = me.Role
            };
            db.CommentActivities.Add(entry);
            c.LastActivityAt = entry.CreatedAt;
            await db.SaveChangesAsync();
            await audit.LogAsync(me, req.Responded ? "CommentMarkedResponded" : "CommentMarkedUnresponded", "Comment", c.Id, null, c.RestaurantId);
        }
        var count = await db.Comments.CountAsync(x => x.GuestKey == c.GuestKey && x.Restaurant.GroupId == me.GroupId);
        return (await Summaries([c], _ => count))[0];
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.DeleteComments)) return Forbid();
        var c = await db.Comments.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null || !me.CanAccessRestaurant(c.RestaurantId)) return NotFound();
        c.IsDeleted = true; c.UpdatedAt = DateTime.UtcNow; c.UpdatedByUserId = me.Id;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "CommentDeleted", "Comment", c.Id, new { Guest = $"{c.GuestFirstName} {c.GuestLastName}", c.VisitDate, c.Reason }, c.RestaurantId);
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] int? restaurantId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] bool? responded)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ExportComments)) return Forbid();
        var ids = ScopedRestaurantIds(me, restaurantId);
        if (ids is null) return Forbid();
        var q = db.Comments.Include(c => c.Restaurant).Where(c => ids.Contains(c.RestaurantId));
        if (from is not null) q = q.Where(c => c.VisitDate >= from);
        if (to is not null) q = q.Where(c => c.VisitDate <= to);
        if (responded is not null) q = q.Where(c => c.Responded == responded);
        var rows = await q.OrderBy(c => c.VisitDate).ThenBy(c => c.Id).ToListAsync();
        var counts = await db.Comments.Where(c => c.Restaurant.GroupId == me.GroupId).GroupBy(c => c.GuestKey)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        var rowIds = rows.Select(r => r.Id).ToList();
        var history = (await db.CommentActivities.Where(a => rowIds.Contains(a.CommentId)).OrderBy(a => a.CreatedAt).ToListAsync())
            .GroupBy(a => a.CommentId)
            .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(a =>
                $"{a.CreatedAt:yyyy-MM-dd} {a.CreatedByName}: {a.Kind}{(a.Method is null ? "" : $" ({a.Method})")}{(a.Text is null ? "" : $" - {a.Text}")}")));

        var sb = new StringBuilder();
        sb.AppendLine("Id,Restaurant,Visit Date,Day Of Week,Guest First Name,Guest Last Name,Phone,Street Address,City,State,Zip,Reason,Reason Details,Coupon Offer,Date Issued,Expires On,Authorized By,Responded,Reply Method,Replied On,Confirmed At,Confirmed By,Notes,Follow-up History,Guest Cards On File,Entered By,Entered At");
        foreach (var c in rows)
        {
            sb.AppendLine(string.Join(',', new[]
            {
                c.Id.ToString(), Csv(c.Restaurant.Name), c.VisitDate.ToString("yyyy-MM-dd"), c.VisitDate.DayOfWeek.ToString(),
                Csv(c.GuestFirstName), Csv(c.GuestLastName), Csv(c.Phone), Csv(c.StreetAddress), Csv(c.City), Csv(c.State), Csv(c.Zip),
                Csv(c.Reason), Csv(c.ReasonDetails), Csv(c.CouponOffer), c.DateIssued.ToString("yyyy-MM-dd"), c.ExpiresOn.ToString("yyyy-MM-dd"),
                Csv(c.AuthorizedBy), c.Responded ? "Yes" : "No", Csv(c.ReplyMethod), c.RepliedOn?.ToString("yyyy-MM-dd") ?? "",
                c.RespondedAt?.ToString("u") ?? "", Csv(c.RespondedByName), Csv(c.Notes), Csv(history.GetValueOrDefault(c.Id)),
                counts.GetValueOrDefault(c.GuestKey, 1).ToString(), Csv(c.CreatedByName), c.CreatedAt.ToString("u")
            }));
        }
        await audit.LogAsync(me, "CommentsExported", "Comment", null, new { restaurantId, from, to, responded, rows = rows.Count }, restaurantId);
        var name = $"guest-comments-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", name);
    }

    // ---- helpers -------------------------------------------------------------------------

    private async Task<List<ActivityDto>> ActivityOf(int commentId) =>
        (await db.CommentActivities.Where(a => a.CommentId == commentId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync())
        .Select(ActivityDto.From).ToList();

    /// <summary>Summaries with follow-up counts and each comment's latest note.</summary>
    private async Task<List<CommentSummary>> Summaries(List<GuestComment> items, Func<GuestComment, int> guestCount)
    {
        var ids = items.Select(c => c.Id).ToList();
        var activity = await db.CommentActivities.Where(a => ids.Contains(a.CommentId) && (a.Kind == ActivityKind.Note || a.Kind == ActivityKind.Attempt))
            .ToListAsync();
        var byComment = activity.GroupBy(a => a.CommentId).ToDictionary(g => g.Key, g => g.ToList());
        return items.Select(c =>
        {
            var mine = byComment.GetValueOrDefault(c.Id) ?? [];
            var latest = mine.Where(a => a.Kind == ActivityKind.Note).MaxBy(a => a.CreatedAt);
            return CommentSummary.From(c, guestCount(c)) with
            {
                NoteCount = mine.Count(a => a.Kind == ActivityKind.Note),
                AttemptCount = mine.Count(a => a.Kind == ActivityKind.Attempt),
                LatestNote = latest is null ? null : ActivityDto.From(latest)
            };
        }).ToList();
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static object Trim(CommentSummary s) => new
    {
        s.GuestFirstName, s.GuestLastName, s.Phone, s.StreetAddress, s.City, s.State, s.Zip, s.VisitDate, s.Reason, s.ReasonDetails,
        s.CouponOffer, s.DateIssued, s.ExpiresOn, s.AuthorizedBy, s.Notes, s.RestaurantId
    };

    private static List<int>? ScopedRestaurantIds(CurrentUser me, int? restaurantId)
    {
        if (restaurantId is null) return me.RestaurantIds.ToList();
        return me.CanAccessRestaurant(restaurantId.Value) ? [restaurantId.Value] : null;
    }

    private static string? Validate(CommentRequest req, GroupSettings settings)
    {
        if (settings.RequirePhoneOrAddress && GuestMatcher.NormalizePhone(req.Phone) is null && GuestMatcher.NormalizeAddress(req.StreetAddress) is null)
            return "Enter the guest's phone number or street address so repeat guests can be recognised.";
        if (!settings.ReasonCategories.Contains(req.Reason, StringComparer.OrdinalIgnoreCase))
            return $"Reason must be one of: {string.Join(", ", settings.ReasonCategories)}.";
        if (req.VisitDate > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)) return "Visit date cannot be in the future.";
        return null;
    }

    private static void Apply(GuestComment c, CommentRequest req, GroupSettings settings)
    {
        c.RestaurantId = req.RestaurantId;
        c.GuestFirstName = req.GuestFirstName.Trim();
        c.GuestLastName = req.GuestLastName.Trim();
        c.StreetAddress = req.StreetAddress?.Trim();
        c.City = req.City?.Trim();
        c.State = req.State?.Trim();
        c.Zip = req.Zip?.Trim();
        c.Phone = req.Phone?.Trim();
        c.CouponOffer = req.CouponOffer?.Trim();
        c.DateIssued = req.DateIssued ?? req.VisitDate;
        c.ExpiresOn = c.DateIssued.AddMonths(settings.CouponValidityMonths);
        c.AuthorizedBy = req.AuthorizedBy?.Trim();
        c.VisitDate = req.VisitDate;
        c.Reason = settings.ReasonCategories.First(r => r.Equals(req.Reason, StringComparison.OrdinalIgnoreCase));
        c.ReasonDetails = req.ReasonDetails?.Trim();
        c.Notes = req.Notes?.Trim();
        c.PhoneNormalized = GuestMatcher.NormalizePhone(req.Phone);
        c.NameNormalized = GuestMatcher.NormalizeName(req.GuestFirstName, req.GuestLastName);
        c.GuestKey = GuestMatcher.BuildGuestKey(req.GuestFirstName, req.GuestLastName, req.Phone, req.StreetAddress, req.Zip);
    }

    /// <summary>
    /// Finds earlier cards for the same guest anywhere in the investment group.
    /// Exact duplicate: same guest, same visit date, same reason (the card was keyed twice).
    /// Likely duplicate: same guest within the configured window.
    /// Repeat guest: the guest already has (threshold - 1) or more cards on file.
    /// </summary>
    private async Task<DuplicateCheckResult> Detect(CurrentUser me, CommentRequest req, GroupSettings settings, int? excludeId, CancellationToken ct)
    {
        var key = GuestMatcher.BuildGuestKey(req.GuestFirstName, req.GuestLastName, req.Phone, req.StreetAddress, req.Zip);
        var phone = GuestMatcher.NormalizePhone(req.Phone);
        var name = GuestMatcher.NormalizeName(req.GuestFirstName, req.GuestLastName);
        var addr = GuestMatcher.NormalizeAddress(req.StreetAddress);

        var q = db.Comments.Include(c => c.Restaurant).Where(c => c.Restaurant.GroupId == me.GroupId);
        if (excludeId is not null) q = q.Where(c => c.Id != excludeId);
        // Match on the primary key, or on phone, or on exact name + address, so a guest who gave a phone
        // on one card and only an address on another is still linked.
        q = q.Where(c => c.GuestKey == key
                         || (phone != null && c.PhoneNormalized == phone)
                         || (addr != null && c.NameNormalized == name && c.StreetAddress != null && c.StreetAddress.ToLower().Contains(addr)));
        var prior = await q.OrderByDescending(c => c.VisitDate).Take(50).ToListAsync(ct);

        // Secondary pass in memory for normalised address equality (SQLite can't run the normaliser).
        if (addr is not null)
        {
            var extra = await db.Comments.Include(c => c.Restaurant)
                .Where(c => c.Restaurant.GroupId == me.GroupId && c.NameNormalized == name && (excludeId == null || c.Id != excludeId))
                .ToListAsync(ct);
            foreach (var e in extra.Where(e => GuestMatcher.NormalizeAddress(e.StreetAddress) == addr && prior.All(p => p.Id != e.Id)))
                prior.Add(e);
        }

        var exact = prior.Where(p => p.VisitDate == req.VisitDate && p.Reason.Equals(req.Reason, StringComparison.OrdinalIgnoreCase)).ToList();
        var window = settings.DuplicateWindowDays;
        var likely = prior.Where(p => Math.Abs(p.VisitDate.DayNumber - req.VisitDate.DayNumber) <= window && !exact.Contains(p)).ToList();
        var priorCount = prior.Count;
        var repeat = priorCount + 1 >= settings.RepeatGuestAlertThreshold;

        // Only show details the caller is allowed to see; team members get counts only.
        var visible = me.Has(ManagerPermission.ViewComments)
            ? prior.Where(p => me.CanAccessRestaurant(p.RestaurantId)).Select(p => CommentSummary.From(p, priorCount)).ToList()
            : [];

        string? message = exact.Count > 0
            ? $"This card looks like it was already entered: {exact[0].GuestFirstName} {exact[0].GuestLastName}, visit on {exact[0].VisitDate:MMM d, yyyy} for \"{exact[0].Reason}\" was saved by {exact[0].CreatedByName} on {exact[0].CreatedAt:MMM d}."
            : likely.Count > 0
                ? $"A card for this guest was entered {Math.Abs(likely[0].VisitDate.DayNumber - req.VisitDate.DayNumber)} day(s) from this visit ({likely[0].Reason} at {likely[0].Restaurant.Name})."
                : repeat
                    ? $"Repeat guest: {priorCount} previous card(s) on file for this guest."
                    : null;

        return new DuplicateCheckResult(exact.Count > 0, likely.Count > 0, repeat, priorCount, settings.RepeatGuestAlertThreshold, key, visible, message);
    }
}
