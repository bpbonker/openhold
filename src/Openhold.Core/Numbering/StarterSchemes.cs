using Openhold.Core.Contracts;

namespace Openhold.Core.Numbering;

/// <summary>
/// Ready-made numbering schemes for common engineering practice. Picking one adds an editable copy to the vault;
/// nothing here is used until an administrator saves it.
/// </summary>
public static class StarterSchemes
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private static IReadOnlyList<NumberingChoice> Codes(params (string Code, string Meaning)[] codes) =>
        codes.Select(c => new NumberingChoice(c.Code, c.Meaning)).ToList();

    public static IReadOnlyList<NumberingScheme> All { get; } =
    [
        new("part-numbers", "Part numbers", "P-{seq:000000}-{check}", false, Day, true)
        {
            StartAt = 100000,
            Description = "One meaningless number per part, the same in every project and the ERP, e.g. P-100000-3. The check digit catches mistyped numbers. "
                + "Best practice for parts reused across products: what the part is lives in its properties, not its number.",
        },
        new("project-models", "Project models", "{field:Project}-{field:Area}-{seq:000}", false, Day, true)
        {
            Description = "Models named by where they sit in a machine, e.g. PRJ01-02-001: project, area, then a count within each area. "
                + "Save under $/…/PRJ01 - Name/02 - Area and the project and area fill themselves in.",
            Fields =
            [
                new("Project", "Project", Hint: "From a folder named \"PRJ01 - Name\"", From: NumberingSources.Folder),
                new("Area", "Area", Codes(("00", "Layout"), ("01", "Frame"), ("02", "Structure"), ("03", "Drive"), ("04", "Access"), ("05", "Hydraulics"),
                    ("06", "Pneumatics"), ("07", "Electrical"), ("08", "Controls"), ("09", "Guarding"), ("10", "Transport"), ("99", "Tooling")),
                    From: NumberingSources.Folder),
            ],
        },
        new("assembly-children", "Parts of an assembly", "{field:Parent}-{seq:000}", false, Day, true)
        {
            Description = "Components named after the assembly they're made in, e.g. PRJ01-02-001-001 under PRJ01-02-001. "
                + "Each assembly counts its own components from 001; sub-assemblies number their own children the same way.",
            Fields = [new("Parent", "Assembly", Hint: "The assembly's number", From: NumberingSources.Parent)],
        },
        new("family-prefix", "Family prefix", "{field:Family}-{seq:00000}", false, Day, true)
        {
            Description = "A short family code and a count per family, e.g. BRK-00012. Easy to read; add families as you need them.",
            Fields =
            [
                new("Family", "Family", Codes(("BRK", "Bracket"), ("PLT", "Plate"), ("SHF", "Shaft"), ("ENC", "Enclosure"), ("TRY", "Cable tray"),
                    ("WLD", "Weldment"), ("ASM", "Assembly"))),
            ],
        },
        new("documents", "Documents by year", "DOC-{yy}-{seq:0000}", false, Day, true)
        {
            Description = "Procedures, reports, calculations and manuals: DOC-26-0001, counting from 1 each year.",
        },
        new("site-drawings", "Numbers per site", "{site}-{seq:00000}", false, Day, true)
        {
            Description = "Each site counts on its own, e.g. AU-00001 and IN-00001, so a number shows where it was issued.",
        },
    ];
}
