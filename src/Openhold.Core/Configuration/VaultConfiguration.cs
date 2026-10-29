using System.Text.Json.Serialization;

namespace Openhold.Core.Configuration;

/// <summary>
/// How a vault is set up: categories, lifecycles, revision schemes, groups, folder rules and item classes. Kept as
/// one document so an administrator's change applies all at once, and every saved version is kept for the record.
/// </summary>
public sealed record VaultConfiguration(
    IReadOnlyList<CategoryDefinition> Categories,
    IReadOnlyList<LifecycleDefinition> Lifecycles,
    IReadOnlyList<RevisionScheme> RevisionSchemes,
    IReadOnlyList<GroupDefinition> Groups,
    IReadOnlyList<FolderRule>? FolderRules = null,
    FolderAccess DefaultFolderAccess = FolderAccess.Change,
    IReadOnlyList<ItemClass>? ItemClasses = null,
    string? CatalogueNumberingSchemeId = null)
{
    public const string AdministratorsGroup = "administrators";

    public ItemClass? ItemClass(string? id) =>
        id is null ? null : (ItemClasses ?? []).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A class with its parents, top first: e.g. Electrical, Circuit breakers. Empty for an unknown class.</summary>
    public IReadOnlyList<ItemClass> ItemClassChain(string? id)
    {
        var chain = new List<ItemClass>();
        for (var current = ItemClass(id); current is not null && chain.Count < 32 && !chain.Contains(current); current = ItemClass(current.ParentId))
            chain.Insert(0, current);
        return chain;
    }

    /// <summary>The attributes items in a class have: its own and every parent's.</summary>
    public IReadOnlyList<ItemAttribute> ItemAttributesFor(string? classId) =>
        ItemClassChain(classId).SelectMany(c => c.Attributes ?? []).ToList();

    /// <summary>
    /// The numbering scheme items in a class get their part numbers from: the nearest one set on the class or a class
    /// above it, else the catalogue's scheme; null when the vault hasn't chosen one.
    /// </summary>
    public string? ItemNumberingSchemeFor(string? classId) =>
        ItemClassChain(classId).Reverse().Select(c => c.NumberingSchemeId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
        ?? (string.IsNullOrWhiteSpace(CatalogueNumberingSchemeId) ? null : CatalogueNumberingSchemeId);

    /// <summary>The catalogue's scheme, and any scheme an item class uses, are issued only by adding catalogue items.</summary>
    public bool NumbersCatalogueItems(string schemeId) =>
        string.Equals(schemeId, CatalogueNumberingSchemeId, StringComparison.OrdinalIgnoreCase)
        || (ItemClasses ?? []).Any(c => string.Equals(c.NumberingSchemeId, schemeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>A class's full name, e.g. "Electrical / Circuit breakers".</summary>
    public string? ItemClassPath(string? classId) =>
        ItemClassChain(classId) is { Count: > 0 } chain ? string.Join(" / ", chain.Select(c => c.Name)) : null;

    /// <summary>
    /// What a person may do in a folder. Rules apply to a folder and everything below it; at the deepest folder with
    /// rules for any of the person's groups, the most generous of those rules applies. Without any rule, the vault's
    /// <see cref="DefaultFolderAccess"/> applies. Administrators may do everything; job workers may read everything.
    /// </summary>
    public FolderAccess AccessTo(string username, string folderPath) => Evaluate(username, folderPath).Access;

    /// <summary>True when no folder is restricted for this person, so nothing needs checking.</summary>
    public bool Unrestricted(string username) =>
        IsAdministrator(username) || (DefaultFolderAccess == FolderAccess.Change && (FolderRules ?? []).All(r => r.Access == FolderAccess.Change));

    /// <summary>
    /// Whether a folder shows in listings: the person may read it, or a rule further down lets them read a folder
    /// below it (so they can get there).
    /// </summary>
    public bool CanSee(string username, string folderPath)
    {
        if (AccessTo(username, folderPath) >= FolderAccess.Read)
            return true;
        var prefix = folderPath.TrimEnd('/') + "/";
        return (FolderRules ?? []).Any(r => r.Access >= FolderAccess.Read && r.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && IsMember(username, r.Group) && AccessTo(username, r.Path) >= FolderAccess.Read);
    }

    /// <summary>A person's access to a folder and, in words, why: for the "Explain why" view.</summary>
    public (FolderAccess Access, string Reason) ExplainAccess(string username, string folderPath) => Evaluate(username, folderPath);

    private (FolderAccess Access, string Reason) Evaluate(string username, string folderPath)
    {
        if (IsAdministrator(username))
            return (FolderAccess.Change, $"{username} is an administrator, so has full access everywhere.");
        var worker = HasPrivilege(username, Configuration.Privileges.RunJobs);
        FolderAccess Floor(FolderAccess access) => worker && access < FolderAccess.Read ? FolderAccess.Read : access;
        var target = folderPath.TrimEnd('/');
        for (var path = target; path.Length > 0; path = ParentOf(path))
        {
            var applying = (FolderRules ?? [])
                .Where(r => string.Equals(r.Path.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase) && IsMember(username, r.Group))
                .ToList();
            if (applying.Count > 0)
            {
                var groups = string.Join(", ", applying.Select(r => $"{Group(r.Group)?.Name ?? r.Group}: {r.Access}"));
                var where = path == target ? $"Rules on {path} ({groups})" : $"Rules on {path} ({groups}), passed down to {target},";
                return (Floor(applying.Max(r => r.Access)), $"{where} decide {username}'s access.");
            }
        }
        return (Floor(DefaultFolderAccess), $"No folder rule applies to {username} here, so the vault default ({DefaultFolderAccess}) applies.");
    }

    /// <summary>The folder above a vault path: <c>$/A/B</c> → <c>$/A</c> → <c>$</c> → empty.</summary>
    private static string ParentOf(string path)
    {
        if (path == "$")
            return "";
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : "$";
    }

    public static VaultConfiguration Empty { get; } = new([], [], [], []);

    public CategoryDefinition? Category(string? id) =>
        id is null ? null : Categories.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public LifecycleDefinition? Lifecycle(string? id) =>
        id is null ? null : Lifecycles.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    public RevisionScheme? RevisionScheme(string? id) =>
        id is null ? null : RevisionSchemes.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    public GroupDefinition? Group(string? id) =>
        id is null ? null : Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The lifecycle files in a category follow, or null if the category has none (or the file has no category).</summary>
    public LifecycleDefinition? LifecycleFor(string? categoryId) => Lifecycle(Category(categoryId)?.LifecycleId);

    /// <summary>
    /// The state a file is in. Versions recorded before the file had a lifecycle carry no state; they are in
    /// the lifecycle's first state. Null when the file has no lifecycle.
    /// </summary>
    public LifecycleState? StateOf(string? categoryId, string? recordedState)
    {
        var lifecycle = LifecycleFor(categoryId);
        if (lifecycle is null)
            return null;
        return recordedState is null ? lifecycle.Initial : lifecycle.State(recordedState) ?? lifecycle.Initial;
    }

    /// <summary>The category a new file gets: the first automatic category whose rules match, in order.</summary>
    public CategoryDefinition? CategoryForNewFile(string folderPath, string fileName) =>
        Categories.FirstOrDefault(c => !c.Manual && c.Matches(folderPath, fileName));

    /// <summary>
    /// True if the user is in the group ("everyone" includes everybody). Administrators are not automatically in every
    /// group: approvals need real members, so an administrator can't sign off as a checker without being one.
    /// </summary>
    public bool IsMember(string username, string groupId)
    {
        if (string.Equals(groupId, "everyone", StringComparison.OrdinalIgnoreCase))
            return true;
        return Group(groupId)?.Members.Contains(username, StringComparer.OrdinalIgnoreCase) == true;
    }

    public bool IsAdministrator(string username) => IsMember(username, AdministratorsGroup);

    /// <summary>True if the person is an administrator or in a group granted the privilege.</summary>
    public bool HasPrivilege(string username, string privilege) =>
        IsAdministrator(username)
        || Groups.Any(g => (g.Privileges ?? []).Contains(privilege, StringComparer.OrdinalIgnoreCase) && IsMember(username, g.Id));

    /// <summary>Every privilege the person has.</summary>
    public IReadOnlyList<string> PrivilegesOf(string username) =>
        Configuration.Privileges.All.Select(p => p.Id).Where(p => HasPrivilege(username, p)).ToList();

    /// <summary>Problems that make this configuration unusable, in words an administrator can act on. Empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Unique(Categories.Select(c => c.Id), "category", problems);
        Unique(Lifecycles.Select(l => l.Id), "lifecycle", problems);
        Unique(RevisionSchemes.Select(r => r.Id), "revision scheme", problems);
        Unique(Groups.Select(g => g.Id), "group", problems);

        foreach (var category in Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.Name))
                problems.Add("Every category needs an id and a name.");
            if (category.LifecycleId is { } lifecycle && Lifecycle(lifecycle) is null)
                problems.Add($"Category '{category.Name}' uses lifecycle '{lifecycle}', which doesn't exist.");
            if (category.RevisionSchemeId is { } scheme && RevisionScheme(scheme) is null)
                problems.Add($"Category '{category.Name}' uses revision scheme '{scheme}', which doesn't exist.");
            if (category.EnforceNaming && string.IsNullOrWhiteSpace(category.NumberingSchemeId) && string.IsNullOrWhiteSpace(category.ChildNumberingSchemeId))
                problems.Add($"Category '{category.Name}' requires names to follow its numbering scheme, but has none.");
            foreach (var extension in category.Extensions ?? [])
            {
                if (!extension.StartsWith('.') || extension.Length < 2)
                    problems.Add($"Category '{category.Name}': '{extension}' should be a file extension such as .dwg.");
            }
        }

        foreach (var lifecycle in Lifecycles)
        {
            foreach (var problem in lifecycle.Validate())
                problems.Add($"Lifecycle '{lifecycle.Name}': {problem}");
            foreach (var group in lifecycle.Transitions.SelectMany(t => (t.Groups ?? []).Concat((t.Approvals ?? []).Select(a => a.Group))))
            {
                if (!string.Equals(group, "everyone", StringComparison.OrdinalIgnoreCase) && Group(group) is null)
                    problems.Add($"Lifecycle '{lifecycle.Name}' refers to group '{group}', which doesn't exist.");
            }
        }

        foreach (var scheme in RevisionSchemes)
        {
            foreach (var problem in scheme.Validate())
                problems.Add($"Revision scheme '{scheme.Name}': {problem}");
        }

        foreach (var group in Groups)
        {
            foreach (var privilege in (group.Privileges ?? []).Where(p => !Configuration.Privileges.All.Any(a => string.Equals(a.Id, p, StringComparison.OrdinalIgnoreCase))))
                problems.Add($"Group '{group.Name}' has an unknown privilege '{privilege}'.");
        }

        foreach (var rule in FolderRules ?? [])
        {
            if (string.IsNullOrWhiteSpace(rule.Path) || !rule.Path.StartsWith('$'))
                problems.Add($"Folder rule path '{rule.Path}' must be a vault path, e.g. $/Projects/Confidential.");
            if (!string.Equals(rule.Group, "everyone", StringComparison.OrdinalIgnoreCase) && Group(rule.Group) is null)
                problems.Add($"A folder rule on {rule.Path} refers to group '{rule.Group}', which doesn't exist.");
        }
        Unique((ItemClasses ?? []).Select(c => c.Id), "item class", problems);
        foreach (var itemClass in ItemClasses ?? [])
        {
            if (string.IsNullOrWhiteSpace(itemClass.Id) || string.IsNullOrWhiteSpace(itemClass.Name))
                problems.Add("Every item class needs an id and a name.");
            if (itemClass.ParentId is { } parent && ItemClass(parent) is null)
                problems.Add($"Item class '{itemClass.Name}' is under '{parent}', which doesn't exist.");
            if (ItemClassChain(itemClass.Id).FirstOrDefault()?.ParentId is { } top && ItemClass(top) is not null)
                problems.Add($"Item class '{itemClass.Name}' is inside itself (its parents go round in a circle).");
            foreach (var duplicate in ItemAttributesFor(itemClass.Id).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"Item class '{itemClass.Name}' has the attribute '{duplicate.Key}' more than once (counting its parents).");
        }

        foreach (var duplicate in (FolderRules ?? []).GroupBy(r => (Path: r.Path.TrimEnd('/').ToUpperInvariant(), Group: r.Group.ToUpperInvariant())).Where(g => g.Count() > 1))
            problems.Add($"There is more than one folder rule for group '{duplicate.First().Group}' on {duplicate.First().Path}.");

        if (Group(AdministratorsGroup) is null)
            problems.Add($"The '{AdministratorsGroup}' group is required.");
        return problems.Distinct().ToList();
    }

    private static void Unique(IEnumerable<string> ids, string what, List<string> problems)
    {
        foreach (var duplicate in ids.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"There is more than one {what} with id '{duplicate.Key}'.");
    }
}

/// <summary>
/// A kind of file, e.g. Engineering, Document or Buyout. The category decides the lifecycle, revision scheme
/// and numbering scheme. New files get the first automatic category whose extensions and folders match;
/// <see cref="Manual"/> categories are only ever chosen by a person (Change Category).
/// The numbering scheme names new files saved through Openhold, and <see cref="ChildNumberingSchemeId"/> (when set)
/// names those made inside an assembly, e.g. its components numbered after it. With <see cref="EnforceNaming"/>, a
/// new file in the category is only accepted at check-in if its name follows one of them (existing files are never
/// affected).
/// </summary>
public sealed record CategoryDefinition(
    string Id,
    string Name,
    string? LifecycleId = null,
    string? RevisionSchemeId = null,
    string? NumberingSchemeId = null,
    IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? Folders = null,
    bool Manual = false,
    bool Library = false,
    IReadOnlyList<string>? Properties = null,
    bool EnforceNaming = false,
    string? ChildNumberingSchemeId = null)
{
    /// <summary>
    /// True when the file's extension is listed (or none are) and its folder matches a pattern (or none are
    /// given). Folder patterns are vault paths where <c>*</c> matches anything, e.g. <c>$/Library/*</c>.
    /// </summary>
    public bool Matches(string folderPath, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (Extensions is { Count: > 0 } && !Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;
        return Folders is not { Count: > 0 } || Folders.Any(pattern => Wildcard.IsMatch(folderPath, pattern));
    }
}

/// <summary>
/// A class of items in the catalogue, e.g. Electrical / Circuit breakers. Classes form a tree; an item in a class
/// has the attributes of the class and all its parents, so parts can be found by their parameters.
/// <see cref="Unspsc"/> is an optional UNSPSC code for ERP spend reports.
/// </summary>
public sealed record ItemClass(string Id, string Name, string? ParentId = null, IReadOnlyList<ItemAttribute>? Attributes = null, string? Unspsc = null,
    string? NumberingSchemeId = null);

/// <summary>
/// A parameter items in a class carry, e.g. Rated current (A). <see cref="Required"/> attributes must be filled in
/// before an item is released; <see cref="Choices"/>, when given, are the only values allowed.
/// </summary>
public sealed record ItemAttribute(string Name, bool Required = false, IReadOnlyList<string>? Choices = null, string? Unit = null);

/// <summary>What a group may do in a folder and everything below it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FolderAccess>))]
public enum FolderAccess
{
    /// <summary>The folder and its files are hidden.</summary>
    None = 0,
    /// <summary>See and get files, but not change them.</summary>
    Read = 1,
    /// <summary>Check out, check in, add, rename, move, delete and change state.</summary>
    Change = 2,
}

/// <summary>A folder permission: <see cref="Group"/> (or "everyone") gets <see cref="Access"/> on <see cref="Path"/> and below.</summary>
public sealed record FolderRule(string Path, string Group, FolderAccess Access);

/// <summary>
/// A set of people, by username, used for transition permissions and approvals. <see cref="Privileges"/> grants
/// its members rights beyond everyday work (see <see cref="Configuration.Privileges"/>); administrators have them all.
/// </summary>
public sealed record GroupDefinition(string Id, string Name, IReadOnlyList<string> Members, IReadOnlyList<string>? Privileges = null);

/// <summary>Rights beyond everyday work, granted to groups. Least privilege: nobody has them unless a group gives them.</summary>
public static class Privileges
{
    public const string ManageConfiguration = "ManageConfiguration";
    public const string ManageUsers = "ManageUsers";
    public const string ChangeCategory = "ChangeCategory";
    public const string DeleteReleasedFiles = "DeleteReleasedFiles";
    public const string PartLibrarian = "PartLibrarian";
    public const string RunJobs = "RunJobs";
    public const string ViewAudit = "ViewAudit";
    public const string ReplicateContent = "ReplicateContent";

    /// <summary>Every privilege, with what it allows, for the Administration window.</summary>
    public static IReadOnlyList<(string Id, string Description)> All { get; } =
    [
        (ManageConfiguration, "Change categories, lifecycles, groups and schemes"),
        (ManageUsers, "Create, disable and reset accounts and two-factor sign-in"),
        (ChangeCategory, "Change a file's category"),
        (DeleteReleasedFiles, "Delete released files"),
        (PartLibrarian, "Approve and release catalogue items"),
        (RunJobs, "Run background jobs (the job worker's account)"),
        (ViewAudit, "Read the audit log and access reviews (e.g. auditors)"),
        (ReplicateContent, "Copy file contents between sites (the site servers' own account)"),
    ];
}

/// <summary>
/// A state a file can be in. A <see cref="Locked"/> file can't be checked out: changing it means moving it
/// back to an unlocked state first. <see cref="Released"/> marks the states that count as released (for
/// "children must be released" and Get Latest Released); <see cref="Obsolete"/> marks files no longer to be used.
/// </summary>
public sealed record LifecycleState(string Name, bool Locked = false, bool Released = false, bool Obsolete = false);

/// <summary>Sign-off needed before a transition happens: <see cref="Required"/> people from <see cref="Group"/>.</summary>
public sealed record ApprovalRule(string Group, int Required = 1);

/// <summary>
/// A permitted move between two states. Nobody changes state except through a transition. Optional
/// conditions are checked first; with <see cref="Approvals"/> the move waits until the sign-offs are in.
/// </summary>
public sealed record LifecycleTransition(
    string From,
    string To,
    string? Label = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<string>? RequiredProperties = null,
    bool RequireChildrenReleased = false,
    bool RequireComment = false,
    IReadOnlyList<ApprovalRule>? Approvals = null,
    bool SubmitterMayApprove = false,
    bool PublishPdf = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"Change to {To}" : Label;

    public bool NeedsApproval => Approvals is { Count: > 0 };

    /// <summary>Whether this person may make (or ask for) this transition. No groups listed means anyone.</summary>
    public bool Allows(VaultConfiguration configuration, string username) =>
        Groups is not { Count: > 0 }
        || configuration.IsAdministrator(username)
        || Groups.Any(g => configuration.IsMember(username, g));
}

/// <summary>A lifecycle: its states (the first is where new files start) and the transitions between them.</summary>
public sealed record LifecycleDefinition(string Id, string Name, IReadOnlyList<LifecycleState> States, IReadOnlyList<LifecycleTransition> Transitions)
{
    [JsonIgnore]
    public LifecycleState Initial => States[0];

    public LifecycleState? State(string? name) =>
        name is null ? null : States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public LifecycleTransition? Transition(string from, string to) =>
        Transitions.FirstOrDefault(t => string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<LifecycleTransition> TransitionsFrom(string from) =>
        Transitions.Where(t => string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
            yield return "it needs an id and a name.";
        if (States.Count == 0)
        {
            yield return "it needs at least one state.";
            yield break;
        }
        if (Initial.Locked)
            yield return $"the first state ('{Initial.Name}') is where new files start, so it can't be locked.";
        foreach (var duplicate in States.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"state '{duplicate.Key}' appears more than once.";
        foreach (var state in States.Where(s => string.IsNullOrWhiteSpace(s.Name)))
            yield return "every state needs a name.";

        foreach (var transition in Transitions)
        {
            if (State(transition.From) is null)
                yield return $"a transition starts from '{transition.From}', which isn't one of its states.";
            if (State(transition.To) is null)
                yield return $"a transition goes to '{transition.To}', which isn't one of its states.";
            if (string.Equals(transition.From, transition.To, StringComparison.OrdinalIgnoreCase))
                yield return $"a transition from '{transition.From}' goes nowhere.";
            foreach (var approval in transition.Approvals ?? [])
            {
                if (approval.Required < 1)
                    yield return $"the approval for '{transition.From}' → '{transition.To}' must need at least one person.";
            }
        }
        foreach (var duplicate in Transitions.GroupBy(t => (t.From.ToUpperInvariant(), t.To.ToUpperInvariant())).Where(g => g.Count() > 1))
            yield return $"there is more than one transition from '{duplicate.First().From}' to '{duplicate.First().To}'.";
    }
}

/// <summary>
/// Matches vault paths against patterns where <c>*</c> matches any run of characters, ignoring case.
/// <c>$/Library/*</c> matches the Library folder itself as well as everything below it.
/// </summary>
public static class Wildcard
{
    public static bool IsMatch(string text, string pattern) =>
        IsMatch(text.AsSpan(), pattern.AsSpan())
        || (pattern.EndsWith("/*", StringComparison.Ordinal) && string.Equals(text, pattern[..^2], StringComparison.OrdinalIgnoreCase));

    private static bool IsMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        // Iterative glob match with backtracking to the last star: linear in practice, no regex needed.
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
