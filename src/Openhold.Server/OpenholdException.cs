namespace Openhold.Server;

/// <summary>
/// A request that cannot be completed for a reason the client can act on.
/// Rendered as RFC 7807 problem details with a stable machine-readable <see cref="Code"/>.
/// </summary>
public sealed class OpenholdException(int statusCode, string code, string message, object? details = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public object? Details { get; } = details;

    public static OpenholdException NotFound(string message, object? details = null) => new(StatusCodes.Status404NotFound, "not_found", message, details);

    public static OpenholdException BadRequest(string code, string message, object? details = null) => new(StatusCodes.Status400BadRequest, code, message, details);

    public static OpenholdException Conflict(string code, string message, object? details = null) => new(StatusCodes.Status409Conflict, code, message, details);
}
