using System.Net;
using System.Text.Json;

namespace Openhold.Client.Sdk;

/// <summary>A request the server rejected, carrying the server's stable error <see cref="Code"/> (e.g. "lock_not_held").</summary>
public sealed class OpenholdApiException(HttpStatusCode statusCode, string code, string message, JsonElement? details)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public JsonElement? Details { get; } = details;
}
