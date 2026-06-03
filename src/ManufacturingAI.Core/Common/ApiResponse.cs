namespace ManufacturingAI.Core.Common;

public record ApiResponse<T>(bool Success, T? Data, string? Error, string? TraceId);
public record ApiResponse(bool Success, string? Error, string? TraceId);
