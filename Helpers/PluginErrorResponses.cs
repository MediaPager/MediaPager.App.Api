using Microsoft.AspNetCore.Mvc;

namespace MediaPager.App.Api.Helpers;

public static class PluginErrorResponses
{
    public const int StatusCode = StatusCodes.Status502BadGateway;

    public static ObjectResult ToProblem(PluginError error)
    {
        var problem = new ProblemDetails
        {
            Title = "Plugin operation failed.",
            Status = StatusCode,
            Detail = error.Message,
        };
        problem.Extensions["pluginError"] = error;
        return new ObjectResult(problem) { StatusCode = StatusCode };
    }
}
