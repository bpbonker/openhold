using Npgsql;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;

namespace Openhold.Server.Tests;

[Collection(VaultCollection.Name)]
public sealed class NumberingTests(VaultFixture vault)
{
    private static string UniqueScheme() => $"S{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Concurrent_reservations_never_issue_the_same_number()
    {
        var admin = vault.Client("admin");
        var scheme = UniqueScheme();
        var prefix = scheme.ToUpperInvariant();
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(scheme, "Electrical", $"{prefix}-{{seq:000000}}", false, TimeSpan.FromHours(1), true));

        var batches = await Task.WhenAll(Enumerable.Range(0, 20).Select(u =>
            vault.Client($"numberer{u}").ReserveNumbersAsync(scheme, count: 5)));

        var all = batches.SelectMany(b => b).Select(r => r.Formatted).ToList();
        Assert.Equal(100, all.Count);
        Assert.Equal(100, all.Distinct().Count());
        Assert.Equal($"{prefix}-000001", all.Order().First());
        Assert.Equal($"{prefix}-000100", all.Order().Last());
    }

    [Fact]
    public async Task Typed_values_follow_the_rules_the_administrator_gave_them()
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var scheme = UniqueScheme();
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(scheme, "Jobs", "J{field:Job}-{seq:00}", false, TimeSpan.FromHours(1), true)
        {
            Fields = [new NumberingField("Job", "Job number", Format: NumberingFormats.Digits, MinLength: 4, MaxLength: 4)],
        });

        Assert.Equal("J1234-01", (await admin.ReserveNumbersAsync(scheme, fields: new Dictionary<string, string> { ["Job"] = "1234" })).Single().Formatted);
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => admin.ReserveNumbersAsync(scheme, fields: new Dictionary<string, string> { ["Job"] = "12A4" }));
        Assert.Equal("Job number must be digits only.", refused.Message);
        refused = await Assert.ThrowsAsync<OpenholdApiException>(() => admin.ReserveNumbersAsync(scheme, fields: new Dictionary<string, string> { ["Job"] = "123" }));
        Assert.Equal("Job number must be 4 characters long.", refused.Message);
        // A default that breaks the field's own rules is refused when the scheme is saved.
        await Assert.ThrowsAsync<OpenholdApiException>(() => admin.SaveNumberingSchemeAsync(new NumberingScheme(UniqueScheme(), "Jobs", "J{field:Job}-{seq:00}", false, TimeSpan.FromHours(1), true)
        {
            Fields = [new NumberingField("Job", Default: "X", Format: NumberingFormats.Digits)],
        }));
    }

    [Fact]
    public async Task Only_a_scheme_nothing_needs_can_be_deleted()
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var unused = UniqueScheme();
        var issued = UniqueScheme();
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(unused, "Never used", $"{unused}-{{seq:000}}", false, TimeSpan.FromHours(1), true));
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(issued, "Issued", $"{issued}-{{seq:000}}", false, TimeSpan.FromHours(1), true));
        await admin.ReserveNumbersAsync(issued);

        await admin.DeleteNumberingSchemeAsync(unused);
        var inUse = await Assert.ThrowsAsync<OpenholdApiException>(() => admin.DeleteNumberingSchemeAsync(issued));
        var used = await Assert.ThrowsAsync<OpenholdApiException>(() => admin.DeleteNumberingSchemeAsync(VaultFixture.CatalogueScheme));
        var notAdmin = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("sam").DeleteNumberingSchemeAsync(issued));

        Assert.DoesNotContain(await admin.ListNumberingSchemesAsync(), s => s.Id == unused);
        Assert.Equal("scheme_in_use", inUse.Code);
        Assert.Equal("scheme_used", used.Code);
        Assert.Contains("the catalogue", used.Message);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, notAdmin.StatusCode);
    }

    [Fact]
    public async Task A_new_vault_has_no_numbering_until_an_administrator_builds_it()
    {
        var sandbox = vault.Client(VaultFixture.Administrator, vault: "Sandbox");

        Assert.Empty(await sandbox.ListNumberingSchemesAsync());
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => sandbox.CreateItemAsync(new SaveItemRequest("Mounting plate", ItemKinds.Made)));
        Assert.Equal("no_catalogue_numbering", refused.Code);
    }

    [Fact]
    public async Task A_category_can_require_new_files_to_follow_its_scheme_and_fields_fill_from_the_folders()
    {
        var admin = vault.Client(VaultFixture.Administrator);
        var schemeId = UniqueScheme();
        var root = VaultFixture.UniqueFolder("naming");
        var folder = $"{root}/PRJ7 - Pump skid/05 - Hydraulics";
        await admin.SaveNumberingSchemeAsync(new NumberingScheme(schemeId, "Project models", "{field:Project}-{field:Area}-{seq:000}", false, TimeSpan.FromHours(1), true)
        {
            Fields =
            [
                new("Project", From: NumberingSources.Folder),
                new("Area", Choices: [new("01", "Frame"), new("05", "Hydraulics")], From: NumberingSources.Folder, Default: "01"),
            ],
        });
        var current = await admin.GetConfigurationAsync();
        await admin.SaveConfigurationAsync(new SaveConfigurationRequest(current.Version, current.Configuration with
        {
            Categories = [new CategoryDefinition($"cat-{schemeId}", "Project model", NumberingSchemeId: schemeId, Folders: [$"{root}/*"], EnforceNaming: true),
                .. current.Configuration.Categories],
        }));
        var saved = (await admin.ListNumberingSchemesAsync()).Single(s => s.Id == schemeId);
        var configuration = (await admin.GetConfigurationAsync()).Configuration;

        var suggestion = Openhold.Core.Numbering.NamingRules.Suggest(configuration, [saved], folder, "new.iam");
        var next = await admin.PreviewNumberAsync(schemeId, suggestion.Fields.ToDictionary(f => f.Field.Name, f => f.Value!));
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => VaultFixture.AddFilesAsync(admin, folder, $"Pump {schemeId}.iam"));
        await VaultFixture.AddFilesAsync(admin, folder, $"{next.Next} - Pump skid.iam");

        Assert.Equal((NumberingSources.Folder, "01"), (saved.Fields[0].From, saved.Fields[1].Default));
        Assert.Equal("PRJ7-05-001", next.Next);
        Assert.Equal("naming_rule", refused.Code);
        Assert.Contains("{field:Project}-{field:Area}-{seq:000}", refused.Message);
    }

    [Fact]
    public async Task Released_numbers_are_not_reused_unless_scheme_allows()
    {
        var user = vault.Client("nina");
        var strict = UniqueScheme();
        var reusing = UniqueScheme();
        await vault.Client(VaultFixture.Administrator).SaveNumberingSchemeAsync(new NumberingScheme(strict, "Strict", $"{strict}-{{seq:0000}}", false, TimeSpan.FromHours(1), true));
        await vault.Client(VaultFixture.Administrator).SaveNumberingSchemeAsync(new NumberingScheme(reusing, "Reusing", $"{reusing}-{{seq:0000}}", true, TimeSpan.FromHours(1), true));

        var s1 = (await user.ReserveNumbersAsync(strict)).Single().Formatted;
        await user.ReleaseNumbersAsync([s1]);
        var s2 = (await user.ReserveNumbersAsync(strict)).Single().Formatted;

        var r1 = (await user.ReserveNumbersAsync(reusing)).Single().Formatted;
        await user.ReleaseNumbersAsync([r1]);
        var r2 = (await user.ReserveNumbersAsync(reusing)).Single().Formatted;

        Assert.NotEqual(s1, s2);
        Assert.Equal(r1, r2);
    }

    [Fact]
    public async Task Checking_in_a_file_named_after_a_reservation_commits_it()
    {
        var user = vault.Client("oscar");
        var scheme = UniqueScheme();
        await vault.Client(VaultFixture.Administrator).SaveNumberingSchemeAsync(new NumberingScheme(scheme, "Docs", $"{scheme}-{{seq:0000}}", true, TimeSpan.FromHours(1), true));
        var number = (await user.ReserveNumbersAsync(scheme)).Single().Formatted;

        await VaultFixture.AddFilesAsync(user, VaultFixture.UniqueFolder("numbered"), $"{number}.dwg");

        // Committed numbers are not released, so they are never reissued even with reuse on.
        Assert.Empty(await user.ReleaseNumbersAsync([number]));
        Assert.Equal("committed", await StateOfAsync(number));
    }

    [Fact]
    public async Task Template_fields_are_required_and_formatted()
    {
        var user = vault.Client("pam");
        var scheme = UniqueScheme();
        await vault.Client(VaultFixture.Administrator).SaveNumberingSchemeAsync(new NumberingScheme(scheme, "Project", $"{{field:Project}}-{{site}}-{scheme}{{seq:000}}", false, TimeSpan.FromHours(1), true));

        var missing = await Assert.ThrowsAsync<OpenholdApiException>(() => user.ReserveNumbersAsync(scheme));
        var issued = (await user.ReserveNumbersAsync(scheme, fields: new Dictionary<string, string> { ["Project"] = "P123" })).Single();

        Assert.Equal("missing_field", missing.Code);
        Assert.Equal($"P123-AU-{scheme}001", issued.Formatted);
    }

    [Fact]
    public async Task Invalid_templates_are_rejected()
    {
        var error = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            vault.Client(VaultFixture.Administrator).SaveNumberingSchemeAsync(new NumberingScheme(UniqueScheme(), "Bad", "NO-SEQUENCE", false, TimeSpan.FromHours(1), true)));
        Assert.Equal("invalid_template", error.Code);
    }

    private OpenholdClient Admin => vault.Client(VaultFixture.Administrator);

    private static NumberingScheme Scheme(string id, string template, params NumberingField[] fields) =>
        new(id, id, template, false, TimeSpan.FromHours(1), true) { Fields = fields };

    private static readonly NumberingField Area = new("Area", "Area", [new("02", "Mast"), new("05", "Hydraulics")]);

    [Fact]
    public async Task Only_administrators_change_numbering_schemes()
    {
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            vault.Client("rita").SaveNumberingSchemeAsync(Scheme(UniqueScheme(), "X-{seq:000}")));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Each_prefix_counts_from_its_own_start_and_codes_come_from_the_list()
    {
        var id = UniqueScheme();
        await Admin.SaveNumberingSchemeAsync(Scheme(id, $"{id}{{field:Area}}-{{seq:000}}{{field:Type}}", Area,
            new NumberingField("Type", "Component", [new("A", "Assembly"), new("P", "Part")])));
        var user = vault.Client("sam");
        async Task<string> Next(string area, string type) =>
            (await user.ReserveNumbersAsync(id, fields: new Dictionary<string, string> { ["Area"] = area, ["Type"] = type })).Single().Formatted;

        var first = await Next("02", "a");
        var second = await Next("02", "P");
        var otherArea = await Next("05", "A");
        var wrongCode = await Assert.ThrowsAsync<OpenholdApiException>(() => Next("99", "A"));

        Assert.Equal($"{id}02-001A", first); // "a" is written as the list writes it
        Assert.Equal($"{id}02-002P", second);
        Assert.Equal($"{id}05-001A", otherArea);
        Assert.Equal("invalid_field", wrongCode.Code);
        Assert.Contains("05 (Hydraulics)", wrongCode.Message);
    }

    [Fact]
    public async Task Children_are_numbered_under_their_parent()
    {
        var id = UniqueScheme();
        await Admin.SaveNumberingSchemeAsync(Scheme(id, "{field:Parent}-{seq:000}"));
        var user = vault.Client("sam");

        var a1 = (await user.ReserveNumbersAsync(id, fields: new Dictionary<string, string> { ["Parent"] = $"{id}-ASM1" })).Single().Formatted;
        var a2 = (await user.ReserveNumbersAsync(id, fields: new Dictionary<string, string> { ["Parent"] = $"{id}-ASM1" })).Single().Formatted;
        var b1 = (await user.ReserveNumbersAsync(id, fields: new Dictionary<string, string> { ["Parent"] = $"{id}-ASM2" })).Single().Formatted;

        Assert.Equal([$"{id}-ASM1-001", $"{id}-ASM1-002", $"{id}-ASM2-001"], [a1, a2, b1]);
    }

    [Fact]
    public async Task Numbers_already_used_by_files_are_skipped()
    {
        var id = UniqueScheme();
        await Admin.SaveNumberingSchemeAsync(Scheme(id, $"{id}-{{seq:000}}"));
        await VaultFixture.AddFilesAsync(Admin, VaultFixture.UniqueFolder("existing"), $"{id}-001.ipt", $"{id}-002 - BRACKET.stp");

        var preview = await vault.Client("sam").PreviewNumberAsync(id);
        var issued = (await vault.Client("sam").ReserveNumbersAsync(id)).Single().Formatted;

        Assert.Equal($"{id}-003", preview.Next);
        Assert.Equal($"{id}-003", issued);
    }

    [Fact]
    public async Task A_scheme_that_has_issued_numbers_keeps_its_structure_but_can_gain_codes()
    {
        var id = UniqueScheme();
        var scheme = Scheme(id, $"{id}{{field:Area}}-{{seq:000}}", Area);
        await Admin.SaveNumberingSchemeAsync(scheme);
        await vault.Client("sam").ReserveNumbersAsync(id, fields: new Dictionary<string, string> { ["Area"] = "02" });

        var reshaped = await Assert.ThrowsAsync<OpenholdApiException>(() => Admin.SaveNumberingSchemeAsync(scheme with { Template = $"{id}-{{field:Area}}-{{seq:0000}}" }));
        var codeRemoved = await Assert.ThrowsAsync<OpenholdApiException>(() =>
            Admin.SaveNumberingSchemeAsync(scheme with { Fields = [new NumberingField("Area", "Area", [new("05", "Hydraulics")])] }));
        var grown = await Admin.SaveNumberingSchemeAsync(scheme with { Fields = [new NumberingField("Area", "Area", [.. Area.Choices!, new("09", "Light tower")])] });

        Assert.Equal(("scheme_in_use", "scheme_in_use"), (reshaped.Code, codeRemoved.Code));
        Assert.Contains("02", codeRemoved.Message);
        Assert.Equal(3, grown.Fields.Single().Choices!.Count);
        Assert.True((await Admin.ListNumberingSchemesAsync()).Single(s => s.Id == id).InUse);
    }

    [Fact]
    public async Task Universal_part_numbers_are_only_issued_by_adding_catalogue_items()
    {
        var refused = await Assert.ThrowsAsync<OpenholdApiException>(() => vault.Client("sam").ReserveNumbersAsync(VaultFixture.CatalogueScheme, count: 500));

        Assert.Equal("catalogue_scheme", refused.Code);
    }

    private async Task<string> StateOfAsync(string formatted)
    {
        await using var connection = new NpgsqlConnection(vault.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT state FROM number_reservations WHERE formatted = @f", connection);
        command.Parameters.AddWithValue("f", formatted);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
