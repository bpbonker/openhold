using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Openhold.Core.Configuration;

namespace Openhold.Desktop.Administration;

/// <summary>Editable copies of the vault configuration, for the Administration window. Saved back as one document.</summary>
public abstract class Editable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed(name);
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected virtual void Changed(string? name)
    {
    }

    /// <summary>"Drawing checkers" → "drawing-checkers": ids for new items, which the configuration refers to them by.</summary>
    public static string Slug(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "item" : slug;
    }

    /// <summary>Splits "a, b; c" or one-per-line text into trimmed, non-empty entries.</summary>
    public static List<string> Split(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>A choice in a drop-down: an id and what people see. An empty id means "none".</summary>
public sealed record Choice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A privilege a group can be given, as a tick box.</summary>
public sealed class PrivilegeChoice(string id, string description) : Editable
{
    private bool _granted;

    public string Id { get; } = id;

    public string Description { get; } = description;

    public bool Granted { get => _granted; set => Set(ref _granted, value); }
}

public sealed class GroupModel : Editable
{
    private string _name = "";
    private string _members = "";

    /// <summary>Every privilege, ticked where this group has it.</summary>
    public IReadOnlyList<PrivilegeChoice> Privileges { get; } =
        Core.Configuration.Privileges.All.Select(p => new PrivilegeChoice(p.Id, p.Description)).ToList();

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Usernames, one per line.</summary>
    public string Members { get => _members; set => Set(ref _members, value); }

    public override string ToString() => Name;

    public static GroupModel From(GroupDefinition group)
    {
        var model = new GroupModel { Id = group.Id, Name = group.Name, Members = string.Join(Environment.NewLine, group.Members) };
        foreach (var privilege in model.Privileges)
            privilege.Granted = (group.Privileges ?? []).Contains(privilege.Id, StringComparer.OrdinalIgnoreCase);
        return model;
    }

    public GroupDefinition ToDefinition()
    {
        var granted = Privileges.Where(p => p.Granted).Select(p => p.Id).ToList();
        return new(Id, Name.Trim(), Split(Members), granted.Count > 0 ? granted : null);
    }
}

public sealed class CategoryModel : Editable
{
    private string _name = "";
    private string _lifecycle = "";
    private string _revisions = "";
    private string _numbering = "";
    private string _extensions = "";
    private string _folders = "";
    private string _properties = "";
    private bool _manual;
    private bool _library;
    private bool _enforceNaming;
    private string _childNumbering = "";

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    // A drop-down pushes null when its list changes under it; that's never a choice ("none" is ""), so it's ignored.
    public string LifecycleId { get => _lifecycle; set => Set(ref _lifecycle, value ?? _lifecycle); }

    public string RevisionSchemeId { get => _revisions; set => Set(ref _revisions, value ?? _revisions); }

    public string NumberingSchemeId { get => _numbering; set => Set(ref _numbering, value ?? _numbering); }

    /// <summary>The scheme for files made inside an assembly, e.g. components numbered after it; empty uses the main scheme.</summary>
    public string ChildNumberingSchemeId { get => _childNumbering; set => Set(ref _childNumbering, value ?? _childNumbering); }

    /// <summary>File extensions, e.g. ".dwg, .ipt"; empty means any.</summary>
    public string Extensions { get => _extensions; set => Set(ref _extensions, value); }

    /// <summary>Vault folder patterns, one per line, e.g. $/Library/*; empty means any folder.</summary>
    public string Folders { get => _folders; set => Set(ref _folders, value); }

    public string Properties { get => _properties; set => Set(ref _properties, value); }

    public bool Manual { get => _manual; set => Set(ref _manual, value); }

    public bool Library { get => _library; set => Set(ref _library, value); }

    /// <summary>New files in this category must be named by its numbering scheme (checked at check-in).</summary>
    public bool EnforceNaming { get => _enforceNaming; set => Set(ref _enforceNaming, value); }

    public override string ToString() => Name;

    public static CategoryModel From(CategoryDefinition category) => new()
    {
        Id = category.Id,
        Name = category.Name,
        LifecycleId = category.LifecycleId ?? "",
        RevisionSchemeId = category.RevisionSchemeId ?? "",
        NumberingSchemeId = category.NumberingSchemeId ?? "",
        Extensions = string.Join(", ", category.Extensions ?? []),
        Folders = string.Join(Environment.NewLine, category.Folders ?? []),
        Properties = string.Join(", ", category.Properties ?? []),
        Manual = category.Manual,
        Library = category.Library,
        EnforceNaming = category.EnforceNaming,
        ChildNumberingSchemeId = category.ChildNumberingSchemeId ?? "",
    };

    public CategoryDefinition ToDefinition() => new(Id, Name.Trim(),
        NullIfEmpty(LifecycleId), NullIfEmpty(RevisionSchemeId), NullIfEmpty(NumberingSchemeId),
        Split(Extensions).Select(e => e.StartsWith('.') ? e : "." + e).ToList(), Split(Folders), Manual, Library, Split(Properties),
        EnforceNaming && !(string.IsNullOrWhiteSpace(NumberingSchemeId) && string.IsNullOrWhiteSpace(ChildNumberingSchemeId)),
        NullIfEmpty(ChildNumberingSchemeId));

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class StateModel : Editable
{
    private string _name = "";
    private bool _locked;
    private bool _released;
    private bool _obsolete;

    /// <summary>Raised with the old and new name, so the lifecycle's changes can follow a rename.</summary>
    public event Action<string, string>? Renamed;

    public string Name
    {
        get => _name;
        set
        {
            var old = _name;
            Set(ref _name, value);
            if (old != value)
                Renamed?.Invoke(old, value);
        }
    }

    public bool Locked { get => _locked; set => Set(ref _locked, value); }

    public bool Released { get => _released; set => Set(ref _released, value); }

    public bool Obsolete { get => _obsolete; set => Set(ref _obsolete, value); }

    public override string ToString() => Name;
}

public sealed class TransitionModel : Editable
{
    private static readonly Regex ApprovalPattern = new(@"^(?<group>.+?)\s*(?:[x×*]\s*(?<count>\d+))?$", RegexOptions.IgnoreCase);

    private string _from = "";
    private string _to = "";
    private string _label = "";
    private string _who = "";
    private string _approvals = "";
    private string _required = "";
    private bool _comment;
    private bool _children;
    private bool _selfApprove;
    private bool _pdf;

    // As for categories: a null from a drop-down whose list changed isn't a choice.
    public string From { get => _from; set => Set(ref _from, value ?? _from); }

    public string To { get => _to; set => Set(ref _to, value ?? _to); }

    public string Label { get => _label; set => Set(ref _label, value); }

    /// <summary>Groups who may make this change, comma separated; empty means anyone.</summary>
    public string Who { get => _who; set => Set(ref _who, value); }

    /// <summary>Sign-off needed, e.g. "Checkers" or "Checkers x2, Quality"; empty means none.</summary>
    public string Approvals { get => _approvals; set => Set(ref _approvals, value); }

    public string RequiredProperties { get => _required; set => Set(ref _required, value); }

    public bool RequireComment { get => _comment; set => Set(ref _comment, value); }

    public bool RequireChildrenReleased { get => _children; set => Set(ref _children, value); }

    public bool SubmitterMayApprove { get => _selfApprove; set => Set(ref _selfApprove, value); }

    public bool PublishPdf { get => _pdf; set => Set(ref _pdf, value); }

    /// <summary>One line for the list, e.g. "Work in Progress → Released: Release (needs Checkers)".</summary>
    public string Summary
    {
        get
        {
            var extras = new List<string>();
            if (Approvals.Trim().Length > 0)
                extras.Add($"approval: {Approvals.Trim()}");
            if (Who.Trim().Length > 0)
                extras.Add($"only {Who.Trim()}");
            if (RequireChildrenReleased)
                extras.Add("parts released");
            if (PublishPdf)
                extras.Add("PDF");
            var label = Label.Trim().Length > 0 ? $": {Label.Trim()}" : "";
            return $"{From} → {To}{label}" + (extras.Count > 0 ? $"   ({string.Join(", ", extras)})" : "");
        }
    }

    protected override void Changed(string? name)
    {
        if (name != nameof(Summary))
            Raise(nameof(Summary));
    }

    public override string ToString() => Summary;

    public static TransitionModel FromDefinition(LifecycleTransition transition, VaultConfiguration configuration) => new()
    {
        _from = transition.From,
        _to = transition.To,
        _label = transition.Label ?? "",
        _who = string.Join(", ", (transition.Groups ?? []).Select(g => configuration.Group(g)?.Name ?? g)),
        _approvals = string.Join(", ", (transition.Approvals ?? []).Select(a =>
            (configuration.Group(a.Group)?.Name ?? a.Group) + (a.Required > 1 ? $" x{a.Required}" : ""))),
        _required = string.Join(", ", transition.RequiredProperties ?? []),
        _comment = transition.RequireComment,
        _children = transition.RequireChildrenReleased,
        _selfApprove = transition.SubmitterMayApprove,
        _pdf = transition.PublishPdf,
    };

    /// <summary>Groups are typed by name or id; they're stored by id.</summary>
    public LifecycleTransition ToDefinition(IReadOnlyList<GroupModel> groups)
    {
        string GroupId(string text) => groups.FirstOrDefault(g => string.Equals(g.Name, text, StringComparison.OrdinalIgnoreCase)
                                                                || string.Equals(g.Id, text, StringComparison.OrdinalIgnoreCase))?.Id
                                       ?? (string.Equals(text, "everyone", StringComparison.OrdinalIgnoreCase) ? "everyone" : text);
        var approvals = Split(Approvals).Select(a => ApprovalPattern.Match(a)).Where(m => m.Success)
            .Select(m => new ApprovalRule(GroupId(m.Groups["group"].Value.Trim()), m.Groups["count"].Success ? int.Parse(m.Groups["count"].Value) : 1))
            .ToList();
        var who = Split(Who).Select(GroupId).ToList();
        return new LifecycleTransition(From, To, Label.Trim().Length > 0 ? Label.Trim() : null,
            who.Count > 0 ? who : null, Split(RequiredProperties) is { Count: > 0 } required ? required : null,
            RequireChildrenReleased, RequireComment, approvals.Count > 0 ? approvals : null, SubmitterMayApprove, PublishPdf);
    }
}

public sealed class LifecycleModel : Editable
{
    private string _name = "";

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    public ObservableCollection<StateModel> States { get; } = [];

    public ObservableCollection<TransitionModel> Transitions { get; } = [];

    public override string ToString() => Name;

    public static LifecycleModel From(LifecycleDefinition lifecycle, VaultConfiguration configuration)
    {
        var model = new LifecycleModel { Id = lifecycle.Id, Name = lifecycle.Name };
        foreach (var state in lifecycle.States)
            model.States.Add(new StateModel { Name = state.Name, Locked = state.Locked, Released = state.Released, Obsolete = state.Obsolete });
        foreach (var transition in lifecycle.Transitions)
            model.Transitions.Add(TransitionModel.FromDefinition(transition, configuration));
        return model;
    }

    public LifecycleDefinition ToDefinition(IReadOnlyList<GroupModel> groups) => new(Id, Name.Trim(),
        States.Select(s => new LifecycleState(s.Name.Trim(), s.Locked, s.Released, s.Obsolete)).ToList(),
        Transitions.Select(t => t.ToDefinition(groups)).ToList());
}

public sealed class RevisionPartModel : Editable
{
    private RevisionPartKind _kind;
    private string _start = "";
    private string _skip = "";
    private string _separator = "";

    public RevisionPartKind Kind { get => _kind; set => Set(ref _kind, value); }

    public string Start { get => _start; set => Set(ref _start, value); }

    public string Skip { get => _skip; set => Set(ref _skip, value); }

    public string Separator { get => _separator; set => Set(ref _separator, value); }
}

public sealed class RevisionSchemeModel : Editable
{
    private string _name = "";

    public RevisionSchemeModel()
    {
        Parts.CollectionChanged += (_, e) =>
        {
            foreach (var part in e.NewItems?.OfType<RevisionPartModel>() ?? [])
                part.PropertyChanged += (_, _) => Raise(nameof(Example));
            Raise(nameof(Example));
        };
    }

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    public ObservableCollection<RevisionPartModel> Parts { get; } = [];

    public static IReadOnlyList<RevisionPartKind> Kinds { get; } = [RevisionPartKind.Letters, RevisionPartKind.Number];

    /// <summary>What revising looks like with this scheme, e.g. "A → B → C … then major: B → C".</summary>
    public string Example
    {
        get
        {
            var scheme = ToDefinition();
            if (scheme.Validate().FirstOrDefault() is { } problem)
                return $"Not valid yet: {problem}";
            var sequence = new List<string> { scheme.First };
            for (var i = 0; i < 4; i++)
                sequence.Add(scheme.Next(sequence[^1]));
            var text = string.Join(" → ", sequence) + " …";
            return scheme.Parts.Count > 1 ? $"{text}    Major revision: {sequence[2]} → {scheme.Next(sequence[2], 0)}" : text;
        }
    }

    public override string ToString() => Name;

    protected override void Changed(string? name)
    {
        if (name != nameof(Example))
            Raise(nameof(Example));
    }

    public static RevisionSchemeModel From(RevisionScheme scheme)
    {
        var model = new RevisionSchemeModel { Id = scheme.Id, Name = scheme.Name };
        foreach (var part in scheme.Parts)
            model.Parts.Add(new RevisionPartModel { Kind = part.Kind, Start = part.Start, Skip = part.Skip, Separator = part.Separator });
        return model;
    }

    public RevisionScheme ToDefinition() => new(Id, Name.Trim(),
        Parts.Select(p => new RevisionPart(p.Kind, p.Start.Trim(), p.Skip.Trim(), p.Separator)).ToList());
}

/// <summary>An item class being edited: where it sits in the tree, and the attributes its items have.</summary>
public sealed class ItemClassModel : Editable
{
    private string _name = "";
    private string _parent = "";
    private string _unspsc = "";

    public string Id { get; set; } = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>The parent class's id; "" for a top-level class.</summary>
    public string ParentId { get => _parent; set => Set(ref _parent, value ?? _parent); }

    public string Unspsc { get => _unspsc; set => Set(ref _unspsc, value); }

    /// <summary>The scheme this class's items are numbered by; "" for the one from the class above, or the catalogue's.</summary>
    public string NumberingSchemeId { get => _numbering; set => Set(ref _numbering, value ?? _numbering); }

    private string _numbering = "";

    public ObservableCollection<ItemAttributeModel> Attributes { get; } = [];

    /// <summary>How far in to draw the class in the list: one step per class above it.</summary>
    public System.Windows.Thickness Indent { get => _indent; set => Set(ref _indent, value); }

    private System.Windows.Thickness _indent;

    public override string ToString() => Name;

    public static ItemClassModel From(ItemClass itemClass)
    {
        var model = new ItemClassModel { Id = itemClass.Id, Name = itemClass.Name, ParentId = itemClass.ParentId ?? "", Unspsc = itemClass.Unspsc ?? "",
            NumberingSchemeId = itemClass.NumberingSchemeId ?? "" };
        foreach (var attribute in itemClass.Attributes ?? [])
            model.Attributes.Add(new ItemAttributeModel { Name = attribute.Name, Required = attribute.Required, Unit = attribute.Unit ?? "", Choices = string.Join(", ", attribute.Choices ?? []) });
        return model;
    }

    public ItemClass ToDefinition() => new(Id, Name.Trim(), ParentId.Length > 0 ? ParentId : null,
        Attributes.Count > 0 ? Attributes.Where(a => a.Name.Trim().Length > 0).Select(a => a.ToDefinition()).ToList() : null,
        Unspsc.Trim().Length > 0 ? Unspsc.Trim() : null, NumberingSchemeId.Length > 0 ? NumberingSchemeId : null);
}

/// <summary>An attribute of an item class: allowed values are typed as a comma-separated list.</summary>
public sealed class ItemAttributeModel : Editable
{
    private string _name = "";
    private bool _required;
    private string _unit = "";
    private string _choices = "";

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool Required { get => _required; set => Set(ref _required, value); }

    public string Unit { get => _unit; set => Set(ref _unit, value); }

    public string Choices { get => _choices; set => Set(ref _choices, value); }

    public ItemAttribute ToDefinition()
    {
        var choices = Choices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new(Name.Trim(), Required, choices.Length > 0 ? choices : null, Unit.Trim().Length > 0 ? Unit.Trim() : null);
    }
}

/// <summary>A folder permission being edited: a group's access to a folder and everything below it.</summary>
public sealed class FolderRuleModel : Editable
{
    private string _path = "$/";
    private string _group = "everyone";
    private FolderAccess _access = FolderAccess.Read;

    public string Path { get => _path; set => Set(ref _path, value); }

    /// <summary>A group id, or "everyone".</summary>
    public string Group
    {
        get => _group;
        set
        {
            if (value is not null) // a drop-down whose list is being refreshed briefly has no selection; keep the group
                Set(ref _group, value);
        }
    }

    public FolderAccess Access { get => _access; set => Set(ref _access, value); }

    public static FolderRuleModel From(FolderRule rule) => new() { Path = rule.Path, Group = rule.Group, Access = rule.Access };

    public FolderRule ToDefinition() => new(Path.Trim().TrimEnd('/'), Group, Access);
}

/// <summary>The whole configuration being edited.</summary>
public sealed class AdminState
{
    public ObservableCollection<GroupModel> Groups { get; } = [];

    public ObservableCollection<CategoryModel> Categories { get; } = [];

    public ObservableCollection<LifecycleModel> Lifecycles { get; } = [];

    public ObservableCollection<RevisionSchemeModel> RevisionSchemes { get; } = [];

    public ObservableCollection<FolderRuleModel> FolderRules { get; } = [];

    public ObservableCollection<ItemClassModel> ItemClasses { get; } = [];

    /// <summary>What everyone may do in folders no rule covers.</summary>
    public FolderAccess DefaultFolderAccess { get; set; } = FolderAccess.Change;

    /// <summary>The scheme catalogue items are numbered by, unless their class has its own; "" for none chosen yet.</summary>
    public string CatalogueNumberingSchemeId { get; set; } = "";

    /// <summary>
    /// The configuration as loaded: anything this window doesn't edit is saved back exactly as it was, so saving
    /// here never drops it.
    /// </summary>
    private VaultConfiguration _loaded = VaultConfiguration.Empty;

    public static AdminState From(VaultConfiguration configuration)
    {
        var state = new AdminState
        {
            _loaded = configuration, DefaultFolderAccess = configuration.DefaultFolderAccess,
            CatalogueNumberingSchemeId = configuration.CatalogueNumberingSchemeId ?? "",
        };
        foreach (var rule in configuration.FolderRules ?? [])
            state.FolderRules.Add(FolderRuleModel.From(rule));
        foreach (var itemClass in configuration.ItemClasses ?? [])
            state.ItemClasses.Add(ItemClassModel.From(itemClass));
        foreach (var group in configuration.Groups)
            state.Groups.Add(GroupModel.From(group));
        foreach (var category in configuration.Categories)
            state.Categories.Add(CategoryModel.From(category));
        foreach (var lifecycle in configuration.Lifecycles)
            state.Lifecycles.Add(LifecycleModel.From(lifecycle, configuration));
        foreach (var scheme in configuration.RevisionSchemes)
            state.RevisionSchemes.Add(RevisionSchemeModel.From(scheme));
        return state;
    }

    public VaultConfiguration ToConfiguration() => _loaded with
    {
        Categories = Categories.Select(c => c.ToDefinition()).ToList(),
        Lifecycles = Lifecycles.Select(l => l.ToDefinition(Groups)).ToList(),
        RevisionSchemes = RevisionSchemes.Select(r => r.ToDefinition()).ToList(),
        Groups = Groups.Select(g => g.ToDefinition()).ToList(),
        FolderRules = FolderRules.Count > 0 ? FolderRules.Select(r => r.ToDefinition()).ToList() : null,
        DefaultFolderAccess = DefaultFolderAccess,
        CatalogueNumberingSchemeId = CatalogueNumberingSchemeId.Length > 0 ? CatalogueNumberingSchemeId : null,
        // An empty list, not none, once the vault has classes: none means "never set up" and gets the defaults back.
        ItemClasses = ItemClasses.Count > 0 || _loaded.ItemClasses is not null ? ItemClasses.Select(c => c.ToDefinition()).ToList() : null,
    };

    /// <summary>An id for a new item that no existing item uses.</summary>
    public static string NewId(string name, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = Editable.Slug(name);
        for (var i = 2; used.Contains(id); i++)
            id = $"{Editable.Slug(name)}-{i}";
        return id;
    }
}
