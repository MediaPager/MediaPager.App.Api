using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Controllers;

[ApiController]
public sealed class PluginActivityController(PluginActivityStore activity) : ControllerBase
{
    [HttpGet("/plugins/activity")]
    public IActionResult List() => Ok(new
    {
        jobs = activity.SnapshotJobs(),
        notifications = activity.SnapshotNotifications(),
    });

    [HttpPost("/plugins/activity/jobs/{id}/cancel")]
    public IActionResult Cancel(string id) =>
        activity.CancelJob(id) ? Ok(new { message = "Cancellation requested." }) : NotFound();

    [HttpDelete("/plugins/activity/jobs/{id}")]
    public IActionResult ClearJob(string id) =>
        activity.RemoveJob(id) ? NoContent() : NotFound();

    [HttpDelete("/plugins/activity/notifications/{id}")]
    public IActionResult DismissNotification(string id) =>
        activity.DismissNotification(id) ? NoContent() : NotFound();
}
