using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// The copies of the site's database: how the last backup went, every copy there is, a copy made now, and a copy
/// downloaded. For System Administrators only: a copy holds everything the site knows. Making and downloading one
/// are audited.
/// </summary>
[ApiController]
[Route("api/system/backups")]
[Authorize(Roles = "SuperAdmin")]
public class SystemBackupsController(DatabaseBackups backups, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    [HttpGet]
    public async Task<BackupsDto> Get(CancellationToken ct) => await Dto(ct);

    [HttpPost]
    public async Task<BackupsDto> Run(CancellationToken ct)
    {
        var me = await users.GetAsync();
        await backups.RunAsync(me.Username, ct);
        return await Dto(ct);
    }

    [HttpGet("{name}/download")]
    public async Task<IActionResult> Download(string name, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (await backups.OpenAsync(name, ct) is not { } stream) return NotFound();
        await audit.LogAsync(me, "DatabaseBackupDownloaded", "Database", null, new { name });
        return File(stream, "application/gzip", name);
    }

    private async Task<BackupsDto> Dto(CancellationToken ct)
    {
        var o = backups.Options;
        return new BackupsDto(o.Enabled, await backups.StateAsync(ct), await backups.ListAsync(ct), backups.OffSiteConfigured, o.HourUtc, o.KeepOnSite, o.KeepDaily, o.KeepMonthly);
    }
}
