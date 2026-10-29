namespace Openhold.Core.Configuration;

/// <summary>
/// The configuration a new vault starts with: a sensible, general set-up that administrators then adjust.
/// Engineering files and documents follow a simple self-release lifecycle; a reviewed lifecycle with
/// approvals and a library lifecycle are ready to switch to. There are no numbering schemes: each company builds
/// its own (Administration › Numbering, optionally from a template) and picks which categories and the catalogue use them.
/// </summary>
public static class DefaultConfiguration
{
    public const string WorkInProgress = "Work in Progress";
    public const string InReview = "In Review";
    public const string Released = "Released";
    public const string Obsolete = "Obsolete";
    public const string Active = "Active";

    /// <summary>Default item classes: a starting tree administrators extend.</summary>
    public static IReadOnlyList<ItemClass> ItemClasses { get; } =
    [
        new("mechanical", "Mechanical", Attributes: [new("Material")]),
        new("machined", "Machined parts", "mechanical", [new("Finish")]),
        new("sheet-metal", "Sheet metal", "mechanical", [new("Thickness", Required: true, Unit: "mm"), new("Finish")]),
        new("fasteners", "Fasteners", "mechanical",
            [new("Type", Required: true, Choices: ["Bolt", "Screw", "Nut", "Washer", "Rivet", "Pin", "Insert"]),
             new("Thread", Required: true), new("Length", Unit: "mm"), new("Grade")]),
        new("bearings", "Bearings and bushes", "mechanical", [new("Bore", Required: true, Unit: "mm"), new("Outside diameter", Unit: "mm")]),
        new("electrical", "Electrical"),
        new("sensors", "Sensors", "electrical",
            [new("Sensing principle", Required: true, Choices: ["Inductive", "Capacitive", "Photoelectric", "Ultrasonic", "Magnetic", "Pressure", "Temperature", "Level", "Flow", "Inclination", "Encoder"]),
             new("Housing"), new("Sensing range", Unit: "mm"), new("Output", Required: true), new("Supply voltage", Required: true), new("Connection"), new("Protection")]),
        new("breakers", "Circuit breakers", "electrical",
            [new("Rated current", Required: true, Unit: "A"), new("Poles", Required: true, Choices: ["1", "2", "3", "4"]),
             new("Curve", Choices: ["B", "C", "D", "K", "Z"]), new("Breaking capacity", Unit: "kA")]),
        new("contactors", "Contactors and relays", "electrical", [new("Coil voltage", Required: true), new("Rated current", Unit: "A")]),
        new("cable", "Cable and wire", "electrical", [new("Cores", Required: true), new("Conductor size", Required: true, Unit: "mm²"), new("Insulation")]),
        new("terminals", "Terminals", "electrical", [new("Conductor size", Unit: "mm²")]),
        new("controls", "PLCs, drives and I/O", "electrical"),
        new("enclosures", "Enclosures", "electrical", [new("Height", Unit: "mm"), new("Width", Unit: "mm"), new("Depth", Unit: "mm"), new("IP rating")]),
        new("assemblies", "Assemblies"),
        new("consumables", "Consumables"),
    ];

    public static VaultConfiguration Create() => new(
        Categories:
        [
            new CategoryDefinition("engineering", "Engineering",
                LifecycleId: "release", RevisionSchemeId: "letters",
                Extensions: [".ipt", ".iam", ".idw", ".ipn", ".ide", ".dwg", ".dxf", ".wdp"],
                Properties: ["Description", "Revision"]),
            new CategoryDefinition("document", "Document",
                LifecycleId: "release", RevisionSchemeId: "letters",
                Properties: ["Description", "Revision"]),
        ],
        Lifecycles:
        [
            new LifecycleDefinition("release", "Release",
                [
                    new LifecycleState(WorkInProgress),
                    new LifecycleState(Released, Locked: true, Released: true),
                    new LifecycleState(Obsolete, Locked: true, Obsolete: true),
                ],
                [
                    new LifecycleTransition(WorkInProgress, Released, "Release", RequireChildrenReleased: true, PublishPdf: true),
                    new LifecycleTransition(Released, WorkInProgress, "Reopen for a new revision"),
                    new LifecycleTransition(Released, Obsolete, "Make obsolete", RequireComment: true),
                    new LifecycleTransition(Obsolete, Released, "Reinstate", Groups: [VaultConfiguration.AdministratorsGroup]),
                ]),
            new LifecycleDefinition("reviewed-release", "Reviewed release",
                [
                    new LifecycleState(WorkInProgress),
                    new LifecycleState(InReview, Locked: true),
                    new LifecycleState(Released, Locked: true, Released: true),
                    new LifecycleState(Obsolete, Locked: true, Obsolete: true),
                ],
                [
                    new LifecycleTransition(WorkInProgress, InReview, "Submit for review", RequireChildrenReleased: true),
                    new LifecycleTransition(InReview, WorkInProgress, "Return for changes", RequireComment: true),
                    new LifecycleTransition(InReview, Released, "Release", RequireChildrenReleased: true,
                        Approvals: [new ApprovalRule("checkers")], PublishPdf: true),
                    new LifecycleTransition(Released, WorkInProgress, "Reopen for a new revision"),
                    new LifecycleTransition(Released, Obsolete, "Make obsolete", RequireComment: true),
                    new LifecycleTransition(Obsolete, Released, "Reinstate", Groups: [VaultConfiguration.AdministratorsGroup]),
                ]),
            new LifecycleDefinition("library", "Library",
                [
                    new LifecycleState(WorkInProgress),
                    new LifecycleState(Active, Locked: true, Released: true),
                    new LifecycleState(Obsolete, Locked: true, Obsolete: true),
                ],
                [
                    new LifecycleTransition(WorkInProgress, Active, "Make active", RequireChildrenReleased: true),
                    new LifecycleTransition(Active, WorkInProgress, "Edit"),
                    new LifecycleTransition(Active, Obsolete, "Make obsolete", RequireComment: true),
                    new LifecycleTransition(Obsolete, Active, "Reinstate", Groups: [VaultConfiguration.AdministratorsGroup]),
                ]),
        ],
        RevisionSchemes:
        [
            new RevisionScheme("letters", "Letters (A, B, C…)", [new RevisionPart(RevisionPartKind.Letters, "A", "IOQSXZ")]),
            new RevisionScheme("number-letter", "Number and letter (1A, 1B… 2A)",
                [new RevisionPart(RevisionPartKind.Number, "1"), new RevisionPart(RevisionPartKind.Letters, "A", "IOQSXZ")]),
            new RevisionScheme("numbers", "Numbers (01, 02, 03…)", [new RevisionPart(RevisionPartKind.Number, "01")]),
            new RevisionScheme("letter-number", "Letter and number (A.01, A.02… B.01)",
                [new RevisionPart(RevisionPartKind.Letters, "A", "IOQSXZ"), new RevisionPart(RevisionPartKind.Number, "01", Separator: ".")]),
        ],
        Groups:
        [
            new GroupDefinition(VaultConfiguration.AdministratorsGroup, "Administrators", []),
            new GroupDefinition("checkers", "Checkers", []),
            new GroupDefinition("job-workers", "Job workers", [], [Privileges.RunJobs]),
            new GroupDefinition("part-librarians", "Part librarians", [], [Privileges.PartLibrarian]),
            new GroupDefinition("site-servers", "Site servers", [], [Privileges.ReplicateContent]),
        ],
        ItemClasses: ItemClasses);
}
