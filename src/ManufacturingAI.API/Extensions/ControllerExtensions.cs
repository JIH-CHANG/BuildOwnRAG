using ManufacturingAI.Core.Common;
using System.Diagnostics;

namespace ManufacturingAI.API.Extensions;

public static class ControllerExtensions
{
    public static string GetTraceId(this Microsoft.AspNetCore.Mvc.ControllerBase _)
        => Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString();

    public static ApiResponse<T> ApiOk<T>(this Microsoft.AspNetCore.Mvc.ControllerBase ctrl, T data)
        => new(true, data, null, ctrl.GetTraceId());

    public static ApiResponse ApiFail(this Microsoft.AspNetCore.Mvc.ControllerBase ctrl, string error)
        => new(false, error, ctrl.GetTraceId());
}
