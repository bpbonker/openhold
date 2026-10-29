using Microsoft.Extensions.Options;

namespace Openhold.Server.Api;

/// <summary>
/// Applied to every endpoint that changes the vault. A backup server holds copies only, so it refuses these
/// with a message that tells the user where to go instead.
/// </summary>
public sealed class RequireWritableServer(IOptions<OpenholdOptions> options) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (options.Value.Role == ServerRole.Backup)
        {
            throw new OpenholdException(StatusCodes.Status403Forbidden, "backup_server",
                $"{options.Value.SiteName} is a backup server: it keeps copies of the vault but doesn't take check-ins or check-outs. " +
                "Connect Openhold to your office server to make changes.");
        }
        return next(context);
    }
}
