using Npgsql;

namespace Openhold.Server.Data;

/// <summary>
/// Database connections for this server. Writes (locks, commits, numbering) always go to
/// <see cref="Primary"/> on the authority; reads that tolerate sub-second replica lag
/// (browsing, history, where-used) use <see cref="Local"/>, which at a remote site is its replica.
/// </summary>
public sealed class OpenholdDb(NpgsqlDataSource primary, NpgsqlDataSource local)
{
    public NpgsqlDataSource Primary { get; } = primary;

    public NpgsqlDataSource Local { get; } = local;
}
