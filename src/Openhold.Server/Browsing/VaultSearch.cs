using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Options;
using Openhold.Core.Contracts;
using Openhold.Server.Configuration;
using Openhold.Server.Data;

namespace Openhold.Server.Browsing;

/// <summary>
/// Vault search over names, folders, recorded properties and check-in comments of current versions.
/// Served from the local database, so searching never crosses the WAN.
/// </summary>
public sealed partial class VaultSearch(OpenholdDb db, IOptions<OpenholdOptions> options)
{
    public const int MaxLimit = 1000;

    private sealed record Term(string? Property, string Pattern, Regex Matcher);

    private sealed record Row(VaultBrowser.FileRow File, string FolderPath, string? Comment);

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var terms = ParseTerms(request.Text);
        if (terms.Count == 0)
            throw OpenholdException.BadRequest("empty_search", "Type something to search for.");
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);

        var conditions = new List<string> { "f.deleted_at IS NULL" };
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(request.Folder) && VaultBrowser.NormalizePath(request.Folder) is var folder && folder != VaultBrowser.RootPath)
        {
            conditions.Add(@"(lower(fo.path) = lower(@folder) OR lower(fo.path) LIKE lower(@folderPrefix) ESCAPE '\')");
            parameters.Add("folder", folder);
            parameters.Add("folderPrefix", EscapeLike(folder) + "/%");
        }

        for (var i = 0; i < terms.Count; i++)
        {
            parameters.Add($"t{i}", terms[i].Pattern);
            if (terms[i].Property is { } property)
            {
                parameters.Add($"k{i}", property);
                conditions.Add($@"EXISTS (SELECT 1 FROM jsonb_each_text(v.vault_properties || v.properties) kv WHERE lower(kv.key) = lower(@k{i}) AND kv.value ILIKE @t{i} ESCAPE '\')");
            }
            else
            {
                conditions.Add($@"(f.name ILIKE @t{i} ESCAPE '\' OR fo.path ILIKE @t{i} ESCAPE '\' OR c.comment ILIKE @t{i} ESCAPE '\'
                    OR EXISTS (SELECT 1 FROM jsonb_each_text(v.vault_properties || v.properties) kv WHERE kv.value ILIKE @t{i} ESCAPE '\'))");
            }
        }
        parameters.Add("limit", limit + 1);

        await using var connection = await db.Local.OpenConnectionAsync(cancellationToken);
        var configuration = (await ConfigurationService.LoadAsync(connection, null, options.Value)).Configuration;
        var rows = (await connection.QueryAsync<VaultBrowser.FileRow, (string FolderPath, string? Comment), Row>($"""
            SELECT {VaultBrowser.FileColumns}, fo.path AS FolderPath, c.comment AS Comment
            FROM files f
            JOIN folders fo ON fo.id = f.folder_id
            {VaultBrowser.FileJoins}
            LEFT JOIN changesets c ON c.id = v.changeset_id
            WHERE {string.Join("\n  AND ", conditions)}
            ORDER BY lower(f.name), lower(fo.path)
            LIMIT @limit
            """, (file, extra) => new Row(file, extra.FolderPath, extra.Comment), parameters, splitOn: "FolderPath")).ToList();

        var hits = rows.Take(limit).Select(r =>
        {
            var info = r.File.ToInfo(configuration);
            return new SearchHit(info, r.FolderPath, DescribeMatch(info, r.Comment, terms));
        }).ToList();
        return new SearchResults(hits, rows.Count > limit);
    }

    /// <summary>
    /// Splits search text into terms. Quotes group words (<c>"12t axle"</c>); <c>Property:value</c>
    /// (or <c>"Part Number":PRJ01*</c>) searches one property; <c>*</c> is a wildcard; every term must match.
    /// </summary>
    private static List<Term> ParseTerms(string text)
    {
        var terms = new List<Term>();
        foreach (Match token in TokenPattern().Matches(text ?? ""))
        {
            var raw = token.Value;
            string? property = null;
            var value = raw;
            var colon = FindPropertySeparator(raw);
            if (colon > 0 && colon < raw.Length - 1)
            {
                property = raw[..colon].Trim('"').Trim();
                value = raw[(colon + 1)..];
            }
            value = value.Trim('"').Trim();
            if (value.Length == 0)
                continue;

            var pattern = "%" + EscapeLike(value).Replace("*", "%") + "%";
            var matcher = new Regex(Regex.Escape(value).Replace(@"\*", ".*"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            terms.Add(new Term(property, pattern, matcher));
        }
        return terms;
    }

    /// <summary>The colon that separates a property name from a value, ignoring colons inside the quoted value.</summary>
    private static int FindPropertySeparator(string token)
    {
        var inQuotes = false;
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] == '"')
                inQuotes = !inQuotes;
            else if (token[i] == ':' && !inQuotes)
                return i;
        }
        return -1;
    }

    /// <summary>Why a file matched, when it isn't obvious from its name, e.g. "Description: AXLE ASSEMBLY".</summary>
    private static string? DescribeMatch(VaultFileInfo file, string? comment, List<Term> terms)
    {
        var reasons = new List<string>();
        foreach (var term in terms)
        {
            if (term.Property is null && term.Matcher.IsMatch(file.Name))
                continue;
            var property = file.Properties.FirstOrDefault(p =>
                (term.Property is null || string.Equals(p.Key, term.Property, StringComparison.OrdinalIgnoreCase)) && term.Matcher.IsMatch(p.Value));
            if (property.Key is not null)
                reasons.Add($"{property.Key}: {property.Value}");
            else if (comment is not null && term.Matcher.IsMatch(comment))
                reasons.Add($"Comment: {comment}");
        }
        return reasons.Count == 0 ? null : string.Join(" · ", reasons.Distinct());
    }

    private static string EscapeLike(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>A token is a run of non-space characters in which quoted parts may contain spaces.</summary>
    [GeneratedRegex("""(?:"[^"]*"|[^\s"])+""")]
    private static partial Regex TokenPattern();
}
