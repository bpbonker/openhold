using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Openhold.Client.Sdk;
using Openhold.Core.Configuration;
using Openhold.Core.Contracts;
using Openhold.Core.Numbering;
using Openhold.Desktop.Administration;

namespace Openhold.Desktop;

/// <summary>
/// Administration: groups, categories, lifecycles and approvals, revision and numbering schemes, jobs, and the
/// history of every configuration change. Only administrators see it; the server refuses anyone else's save.
/// The configuration is edited as a whole and saved as one new version, so it never ends up half-changed.
/// </summary>
public partial class AdministrationWindow : Window
{
    private int _basedOnVersion;
    private string _savedJson = "";
    private AdminState _state = new();
    private List<NumberingScheme> _numbering = [];

    /// <param name="itemClasses">Open on the Item Classes tab, e.g. from the Catalogue's Manage Classes.</param>
    public AdministrationWindow(bool itemClasses = false)
    {
        InitializeComponent();
        DataContext = this;
        if (itemClasses)
            Tabs.SelectedItem = ItemClassesTab;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) =>
        {
            if (!App.Unattended && HasUnsavedChanges() && MessageBox.Show(this, "Close without saving your changes?", "Openhold: Administration",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>True once a change was saved, so Explorer reloads the configuration.</summary>
    public bool Saved { get; private set; }

    public ObservableCollection<GroupModel> Groups => _state.Groups;
    public ObservableCollection<CategoryModel> Categories => _state.Categories;
    public ObservableCollection<LifecycleModel> Lifecycles => _state.Lifecycles;
    public ObservableCollection<RevisionSchemeModel> RevisionSchemes => _state.RevisionSchemes;

    public ObservableCollection<Choice> LifecycleChoices { get; } = [];
    public ObservableCollection<Choice> RevisionChoices { get; } = [];
    public ObservableCollection<Choice> NumberingChoices { get; } = [];
    public ObservableCollection<Choice> CatalogueNumberingChoices { get; } = [];
    public ObservableCollection<Choice> ClassNumberingChoices { get; } = [];

    /// <summary>The configuration being edited, for settings bound straight to it (e.g. the catalogue's numbering).</summary>
    public AdminState State => _state;

    private async Task LoadAsync(VaultConfiguration? replacement = null)
    {
        try
        {
            var current = await App.Client.GetConfigurationAsync();
            _basedOnVersion = current.Version;
            _savedJson = Baseline(current.Configuration);
            _numbering = (await App.Client.ListNumberingSchemesAsync()).ToList();
            NumberingList.ItemsSource = _numbering;
            Show(replacement ?? current.Configuration);
            KnownUsers.ItemsSource = await App.Client.ListUsersAsync();
            await LoadAccountsAsync();
            await LoadJobsAsync();
            await LoadSitesAsync();
            await LoadErpAsync();
            if (AuditTab.Visibility == Visibility.Visible)
                ShowAudit_Click(this, new RoutedEventArgs());
            ExplainPerson.ItemsSource = KnownUsers.ItemsSource;
            History.ItemsSource = await App.Client.GetConfigurationHistoryAsync();
        }
        catch (Exception ex) when (ex is OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            ShowProblem($"Couldn't load the configuration: {ex.Message}");
        }
    }

    /// <summary>Puts a configuration into the editor, keeping the selected tab's list positions where possible.</summary>
    private void Show(VaultConfiguration configuration)
    {
        _state = AdminState.From(configuration);
        foreach (var lifecycle in _state.Lifecycles)
            FollowStateRenames(lifecycle);
        // The drop-downs' lists must be filled before they're bound, or they can't show the saved choices.
        RefreshChoices();
        DataContext = null;
        DataContext = this;
        GroupList.SelectedIndex = CategoryList.SelectedIndex = LifecycleList.SelectedIndex = SchemeList.SelectedIndex = ItemClassList.SelectedIndex = 0;
        DefaultAccess.SelectedItem = DefaultAccess.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == _state.DefaultFolderAccess.ToString());
        RefreshNamingRules();
        AuditTab.Visibility = App.Configuration.HasPrivilege(App.Client.Username, Privileges.ViewAudit) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Serialize(VaultConfiguration configuration) => JsonSerializer.Serialize(configuration);

    /// <summary>
    /// The saved configuration as the editor would write it back (e.g. empty lists rather than none), so opening
    /// and closing the window without touching anything never counts as a change.
    /// </summary>
    private static string Baseline(VaultConfiguration configuration) => Serialize(AdminState.From(configuration).ToConfiguration());

    private bool HasUnsavedChanges()
    {
        try
        {
            return Serialize(_state.ToConfiguration()) != _savedJson;
        }
        catch (Exception)
        {
            return true; // something half-typed (e.g. a bad number) is still a change
        }
    }

    /// <summary>Drop-down choices for categories, rebuilt in place so the selected values stay put.</summary>
    private void RefreshChoices()
    {
        Sync(LifecycleChoices, _state.Lifecycles.Select(l => new Choice(l.Id, l.Name)), "(no lifecycle)");
        Sync(RevisionChoices, _state.RevisionSchemes.Select(r => new Choice(r.Id, r.Name)), "(no revisions)");
        Sync(NumberingChoices, _numbering.Select(n => new Choice(n.Id, $"{n.Name}  (e.g. {NamingChoices.Example(n)})")), "(no numbering)");
        Sync(CatalogueNumberingChoices, NumberingChoices.Skip(1), "(none yet: new items can't be added)");
        Sync(ClassNumberingChoices, NumberingChoices.Skip(1), "(as the class above, or the catalogue)");
        Sync(GroupChoices, _state.Groups.Select(g => new Choice(g.Id, g.Name)), "Everyone", "everyone");
        Sync(ItemClassChoices, _state.ItemClasses.Select(c => new Choice(c.Id, c.Name)), "(top level)");
        foreach (var itemClass in _state.ItemClasses)
        {
            var depth = 0;
            for (var parent = itemClass.ParentId; parent.Length > 0 && depth < 8; depth++)
                parent = _state.ItemClasses.FirstOrDefault(c => c.Id == parent)?.ParentId ?? "";
            itemClass.Indent = new Thickness(depth * 16, 0, 0, 0);
        }
    }

    private static void Sync(ObservableCollection<Choice> target, IEnumerable<Choice> items, string none, string noneId = "")
    {
        var wanted = new[] { new Choice(noneId, none) }.Concat(items).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && target[i] == wanted[i])
                continue;
            if (i < target.Count)
                target.Insert(i, wanted[i]);
            else
                target.Add(wanted[i]);
        }
        while (target.Count > wanted.Count)
            target.RemoveAt(target.Count - 1);
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs)
            return;
        RefreshChoices();
        if (Tabs.SelectedItem == NamingTab)
            RefreshNamingRules();
    }

    /// <summary>The overview reflects the categories and schemes as edited, including ones added since it was last shown.</summary>
    private void RefreshNamingRules() =>
        NamingRulesGrid.ItemsSource = Categories.Select(c => new NamingRuleRow(c, SchemeById)).ToList();

    private NumberingScheme? SchemeById(string id) => _numbering.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));

    // ---- Groups ----

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = new GroupModel { Id = AdminState.NewId("new group", Groups.Select(g => g.Id)), Name = "New group" };
        Groups.Add(group);
        GroupList.SelectedItem = group;
    }

    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupModel group)
            return;
        if (group.Id == VaultConfiguration.AdministratorsGroup)
        {
            ShowProblem("The Administrators group can't be removed.");
            return;
        }
        Groups.Remove(group);
    }

    private void AddMember_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupModel group)
            return;
        var members = Editable.Split(group.Members);
        foreach (var user in KnownUsers.SelectedItems.Cast<UserInfo>())
        {
            if (!members.Contains(user.Username, StringComparer.OrdinalIgnoreCase))
                members.Add(user.Username);
        }
        group.Members = string.Join(Environment.NewLine, members);
    }

    // ---- Users (accounts are managed through their own endpoints, saved straight away) ----

    private async Task LoadAccountsAsync(string? select = null)
    {
        try
        {
            var rows = (await App.Client.ListAccountsAsync()).Select(a => new AccountRow(a)).ToList();
            Accounts.ItemsSource = rows;
            Accounts.SelectedItem = rows.FirstOrDefault(r => r.Account.Username == select);
            UsersTab.Visibility = Visibility.Visible;
        }
        catch (OpenholdApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            UsersTab.Visibility = Visibility.Collapsed; // needs the Manage Users privilege
        }
    }

    private AccountInfo? SelectedAccount => (Accounts.SelectedItem as AccountRow)?.Account;

    /// <summary>Runs an account change, shows the outcome, and reloads the list keeping the selection.</summary>
    private async Task ChangeAccountAsync(string username, Func<Task> change, string done)
    {
        try
        {
            await change();
            ShowProblem(null, done);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
        await LoadAccountsAsync(username);
    }

    private bool Confirm(string question) =>
        MessageBox.Show(this, question, "Openhold: Users", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Shows a one-time temporary password, copied to the clipboard so it can be passed on securely.</summary>
    private void ShowTemporaryPassword(string username, string password)
    {
        Clipboard.SetText(password);
        MessageBox.Show(this,
            $"Temporary password for {username}:\n\n{password}\n\nIt's been copied to the clipboard. Give it to the person privately (not by email). " +
            "They'll choose their own password when they first sign in. It won't be shown again.",
            "Openhold: Temporary password", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void NewAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewAccountWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Request is not { } request)
            return;
        try
        {
            var created = await App.Client.CreateAccountAsync(request);
            ShowTemporaryPassword(created.Account.Username, created.TemporaryPassword);
            await LoadAccountsAsync(created.Account.Username);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    private async void ToggleDisabled_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } account)
            return;
        if (!account.Disabled && !Confirm($"Disable {account.Username}? They're stopped straight away, however they sign in, until you enable the account again."))
            return;
        await ChangeAccountAsync(account.Username, () => App.Client.UpdateAccountAsync(account.Username, new UpdateAccountRequest(Disabled: !account.Disabled)),
            account.Disabled ? $"{account.Username} can sign in again." : $"{account.Username} is disabled.");
    }

    private async void ToggleTwoFactorRequired_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } account)
            return;
        await ChangeAccountAsync(account.Username,
            () => App.Client.UpdateAccountAsync(account.Username, new UpdateAccountRequest(TwoFactorRequired: !account.TwoFactorRequired)),
            account.TwoFactorRequired ? $"Two-factor sign-in is no longer required for {account.Username}."
                : $"{account.Username} must set up two-factor sign-in next time they sign in.");
    }

    private async void ResetTwoFactor_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } account || !Confirm($"Reset two-factor sign-in for {account.Username}? They'll set it up again (e.g. on a new phone) and are signed out now."))
            return;
        await ChangeAccountAsync(account.Username, () => App.Client.ResetTwoFactorAsync(account.Username), $"Two-factor sign-in reset for {account.Username}.");
    }

    private async void ResetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } account || !Confirm($"Reset {account.Username}'s password? They're signed out now and choose a new password with the temporary one."))
            return;
        try
        {
            var reset = await App.Client.ResetPasswordAsync(account.Username);
            ShowTemporaryPassword(account.Username, reset.TemporaryPassword);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
        await LoadAccountsAsync(account.Username);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is { } account)
            await ChangeAccountAsync(account.Username, () => App.Client.UnlockAccountAsync(account.Username), $"{account.Username} is unlocked.");
    }

    private async void EndSessions_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not { } account || !Confirm($"Sign {account.Username} out on every PC? They'll need to sign in again."))
            return;
        await ChangeAccountAsync(account.Username, () => App.Client.EndSessionsAsync(account.Username), $"{account.Username} is signed out everywhere.");
    }

    /// <summary>An account as a row in the Users list.</summary>
    private sealed class AccountRow(AccountInfo account)
    {
        public AccountInfo Account { get; } = account;
        public string Username => Account.Username;
        public string DisplayName => Account.DisplayName;
        public string Method => Account.Kind switch { "local" => "Openhold account", "oidc" => "Organisation account", _ => "Windows" };
        public string Status => Account.Disabled ? "Disabled" : Account.Locked ? "Locked" : Account.MustChangePassword ? "Must change password" : "Active";
        public string TwoFactor => Account.Kind != "local" ? "By their organisation" : Account.TwoFactorEnabled ? "On" : Account.TwoFactorRequired ? "Required, not set up" : "Off";
        public string LastSignIn => Account.LastSignInAt?.ToLocalTime().ToString("d MMM yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture) ?? "Never";
        public int Sessions => Account.Sessions;
        public override string ToString() => Username;
    }

    // ---- Categories ----

    private void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var category = new CategoryModel { Id = AdminState.NewId("new category", Categories.Select(c => c.Id)), Name = "New category", Manual = true };
        Categories.Add(category);
        CategoryList.SelectedItem = category;
    }

    private void RemoveCategory_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryList.SelectedItem is CategoryModel category)
            Categories.Remove(category);
    }

    private void CategoryUp_Click(object sender, RoutedEventArgs e) => Move(Categories, CategoryList, -1);

    private void CategoryDown_Click(object sender, RoutedEventArgs e) => Move(Categories, CategoryList, +1);

    private static void Move<T>(ObservableCollection<T> items, System.Windows.Controls.Primitives.Selector list, int by)
    {
        var index = list.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= items.Count)
            return;
        items.Move(index, index + by);
        list.SelectedIndex = index + by;
    }

    // ---- Lifecycles ----

    private LifecycleModel? Lifecycle => LifecycleList.SelectedItem as LifecycleModel;

    private void AddLifecycle_Click(object sender, RoutedEventArgs e)
    {
        var lifecycle = new LifecycleModel { Id = AdminState.NewId("new lifecycle", Lifecycles.Select(l => l.Id)), Name = "New lifecycle" };
        lifecycle.States.Add(new StateModel { Name = DefaultConfiguration.WorkInProgress });
        lifecycle.States.Add(new StateModel { Name = DefaultConfiguration.Released, Locked = true, Released = true });
        lifecycle.Transitions.Add(new TransitionModel { From = DefaultConfiguration.WorkInProgress, To = DefaultConfiguration.Released, Label = "Release" });
        lifecycle.Transitions.Add(new TransitionModel { From = DefaultConfiguration.Released, To = DefaultConfiguration.WorkInProgress, Label = "Reopen for a new revision" });
        FollowStateRenames(lifecycle);
        Lifecycles.Add(lifecycle);
        LifecycleList.SelectedItem = lifecycle;
    }

    private void CopyLifecycle_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is not { } source)
            return;
        var configuration = _state.ToConfiguration();
        var copy = LifecycleModel.From(source.ToDefinition(Groups) with { Id = AdminState.NewId(source.Name + " copy", Lifecycles.Select(l => l.Id)) }, configuration);
        copy.Name = source.Name + " (copy)";
        FollowStateRenames(copy);
        Lifecycles.Add(copy);
        LifecycleList.SelectedItem = copy;
    }

    private void RemoveLifecycle_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is not { } lifecycle)
            return;
        if (Categories.FirstOrDefault(c => c.LifecycleId == lifecycle.Id) is { } user)
        {
            ShowProblem($"The {user.Name} category uses {lifecycle.Name}. Give that category another lifecycle first.");
            return;
        }
        Lifecycles.Remove(lifecycle);
    }

    /// <summary>Renaming a state renames it in the changes that use it, so the flow stays intact.</summary>
    private static void FollowStateRenames(LifecycleModel lifecycle)
    {
        void Watch(StateModel state) => state.Renamed += (from, to) =>
        {
            foreach (var transition in lifecycle.Transitions)
            {
                if (string.Equals(transition.From, from, StringComparison.Ordinal))
                    transition.From = to;
                if (string.Equals(transition.To, from, StringComparison.Ordinal))
                    transition.To = to;
            }
        };
        foreach (var state in lifecycle.States)
            Watch(state);
        lifecycle.States.CollectionChanged += (_, e) =>
        {
            foreach (var state in e.NewItems?.OfType<StateModel>() ?? [])
                Watch(state);
        };
    }

    private void AddState_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is not { } lifecycle)
            return;
        var state = new StateModel { Name = "New state", Locked = true };
        lifecycle.States.Add(state);
        StateGrid.SelectedItem = state;
    }

    private void RemoveState_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is not { } lifecycle || StateGrid.SelectedItem is not StateModel state)
            return;
        lifecycle.States.Remove(state);
        foreach (var transition in lifecycle.Transitions.Where(t => t.From == state.Name || t.To == state.Name).ToList())
            lifecycle.Transitions.Remove(transition);
    }

    private void StateUp_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is { } lifecycle)
            Move(lifecycle.States, StateGrid, -1);
    }

    private void StateDown_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is { } lifecycle)
            Move(lifecycle.States, StateGrid, +1);
    }

    private void AddTransition_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is not { States.Count: > 0 } lifecycle)
            return;
        var transition = new TransitionModel { From = lifecycle.States[0].Name, To = lifecycle.States[^1].Name };
        lifecycle.Transitions.Add(transition);
        TransitionList.SelectedItem = transition;
    }

    private void RemoveTransition_Click(object sender, RoutedEventArgs e)
    {
        if (Lifecycle is { } lifecycle && TransitionList.SelectedItem is TransitionModel transition)
            lifecycle.Transitions.Remove(transition);
    }

    // ---- Revision schemes ----

    private void AddScheme_Click(object sender, RoutedEventArgs e)
    {
        var scheme = new RevisionSchemeModel { Id = AdminState.NewId("new scheme", RevisionSchemes.Select(r => r.Id)), Name = "New scheme" };
        scheme.Parts.Add(new RevisionPartModel { Kind = RevisionPartKind.Letters, Start = "A", Skip = "IOQSXZ" });
        RevisionSchemes.Add(scheme);
        SchemeList.SelectedItem = scheme;
    }

    private void RemoveScheme_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeList.SelectedItem is not RevisionSchemeModel scheme)
            return;
        if (Categories.FirstOrDefault(c => c.RevisionSchemeId == scheme.Id) is { } user)
        {
            ShowProblem($"The {user.Name} category uses {scheme.Name}. Give that category another revision scheme first.");
            return;
        }
        RevisionSchemes.Remove(scheme);
    }

    private void AddPart_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeList.SelectedItem is RevisionSchemeModel scheme)
            scheme.Parts.Add(new RevisionPartModel { Kind = RevisionPartKind.Number, Start = "1", Separator = scheme.Parts.Count > 0 ? "." : "" });
    }

    private void RemovePart_Click(object sender, RoutedEventArgs e)
    {
        if (SchemeList.SelectedItem is RevisionSchemeModel scheme && PartGrid.SelectedItem is RevisionPartModel part)
            scheme.Parts.Remove(part);
    }

    // ---- Numbering (saved scheme by scheme, through its own endpoint) ----

    private NumberingScheme? _editingNumbering;
    private ObservableCollection<SchemePartRow> _parts = [];
    private bool _showingPart;

    private void NumberingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _editingNumbering = NumberingList.SelectedItem as NumberingScheme;
        var inUse = _editingNumbering?.InUse == true;
        _showingPart = true;
        NumberingEditor.IsEnabled = _editingNumbering is not null;
        NumberingName.Text = _editingNumbering?.Name ?? "";
        NumberingDescription.Text = _editingNumbering?.Description ?? "";
        NumberingStart.Text = (_editingNumbering?.StartAt ?? 1).ToString(System.Globalization.CultureInfo.CurrentCulture);
        NumberingInUse.Visibility = inUse ? Visibility.Visible : Visibility.Collapsed;
        NumberingTemplate.IsReadOnly = NumberingStart.IsReadOnly = inUse;
        PartButtons.IsEnabled = !inUse;
        NumberingReuse.IsChecked = _editingNumbering?.AllowReuse == true;
        NumberingEnabled.IsChecked = _editingNumbering?.Enabled != false;
        try
        {
            _parts = new ObservableCollection<SchemePartRow>(_editingNumbering is null ? [] : SchemePartRow.From(_editingNumbering));
        }
        catch (FormatException ex)
        {
            _parts = [];
            ShowProblem($"{_editingNumbering!.Name} can't be shown part by part: {ex.Message}");
        }
        PartList.ItemsSource = _parts;
        _showingPart = false;
        PartList.SelectedIndex = _parts.Count > 0 ? 0 : -1;
        ShowPart();
        RefreshNumberingExample();
    }

    private void PartList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPart();

    /// <summary>Shows the selected part's settings: only those its kind has.</summary>
    private void ShowPart()
    {
        var part = PartList.SelectedItem as SchemePartRow;
        var structureFixed = _editingNumbering?.InUse == true;
        _showingPart = true;
        if (PartType.ItemsSource is null)
            PartType.ItemsSource = SchemePartRow.Types.Select(t => t.Name).ToList();
        PartEditor.DataContext = part;
        if (part is not null)
            PartList.ScrollIntoView(part);
        PartEditor.IsEnabled = part is not null;
        PartType.SelectedIndex = part is null ? -1 : (int)part.Type;
        PartType.IsEnabled = PartTextPanel.IsEnabled = PartNameBox.IsEnabled = PartWidthPanel.IsEnabled = PartYearPanel.IsEnabled = !structureFixed;
        _showingPart = false;

        var type = part?.Type;
        PartTypeHint.Text = part is null ? "Add a part to start building the number." : SchemePartRow.Types[(int)part.Type].Description;
        static Visibility Shown(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
        PartTextPanel.Visibility = Shown(type == SchemePartType.FixedText);
        PartNamePanel.Visibility = Shown(part?.IsField == true);
        PartCodesPanel.Visibility = Shown(type is SchemePartType.PickList or SchemePartType.FolderCode);
        PartCodesLabel.Text = type == SchemePartType.FolderCode ? "Codes the folders may use (optional)" : "Codes";
        PartCodesHint.Text = type == SchemePartType.FolderCode
            ? "The code is the text before \" - \" in a folder's name, e.g. 02 from \"02 - Mast\". Listing the codes stops a mistyped folder from making a wrong number; leave the list empty to take any code. Parts side by side can share one folder code, e.g. PR01 gives P, R and 01."
            : "People choose one of these codes when they save a new file. Codes can be added later, but not taken away once numbers use them.";
        PartRulesPanel.Visibility = Shown(type == SchemePartType.Typed);
        PartDefaultPanel.Visibility = Shown(type is SchemePartType.PickList or SchemePartType.Typed or SchemePartType.FolderCode);
        PartWidthPanel.Visibility = Shown(type == SchemePartType.Counter);
        PartYearPanel.Visibility = Shown(type == SchemePartType.Year);
    }

    private void PartType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingPart || PartList.SelectedItem is not SchemePartRow part || PartType.SelectedIndex < 0)
            return;
        part.Type = (SchemePartType)PartType.SelectedIndex;
        if (part.IsField && part.Name.Length == 0)
            part.Name = UniquePartName(part.Type);
        ShowPart();
        RefreshNumberingExample();
    }

    /// <summary>Any setting of a part (or the first number) changed: show the numbers it now makes, once the change has reached the part.</summary>
    private void PartSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_showingPart)
            Dispatcher.BeginInvoke(RefreshNumberingExample, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PartCodes_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => PartSetting_Changed(this, new RoutedEventArgs());

    private string UniquePartName(SchemePartType type)
    {
        var name = type switch
        {
            SchemePartType.PickList => "Code",
            SchemePartType.FolderCode => "Folder",
            SchemePartType.ParentNumber => "Parent",
            _ => "Value",
        };
        var unique = name;
        for (var i = 2; _parts.Any(p => p.IsField && string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
            unique = $"{name}{i}";
        return unique;
    }

    /// <summary>Offers the kinds of part; the one picked goes after the selected part.</summary>
    private void AddSchemePart_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddPartButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (type, name, description) in SchemePartRow.Types)
        {
            var item = new MenuItem { Header = name, ToolTip = description };
            item.Click += (_, _) =>
            {
                var part = new SchemePartRow(type);
                if (part.IsField)
                    part.Name = UniquePartName(type);
                if (type == SchemePartType.FixedText)
                    part.Text = "-";
                _parts.Insert(PartList.SelectedIndex < 0 ? _parts.Count : PartList.SelectedIndex + 1, part);
                PartList.SelectedItem = part;
                RefreshNumberingExample();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void MovePartUp_Click(object sender, RoutedEventArgs e) => MovePart(-1);

    private void MovePartDown_Click(object sender, RoutedEventArgs e) => MovePart(1);

    private void MovePart(int by)
    {
        var index = PartList.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= _parts.Count)
            return;
        _parts.Move(index, index + by);
        PartList.SelectedIndex = index + by;
        RefreshNumberingExample();
    }

    private void RemoveSchemePart_Click(object sender, RoutedEventArgs e)
    {
        if (PartList.SelectedItem is not SchemePartRow part)
            return;
        var index = PartList.SelectedIndex;
        _parts.Remove(part);
        PartList.SelectedIndex = Math.Min(index, _parts.Count - 1);
        RefreshNumberingExample();
    }

    /// <summary>Rebuilds the parts from typed template text, keeping the settings of values already defined.</summary>
    private void ApplyTemplateText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var fields = _parts.Select(p => p.ToField()).OfType<NumberingField>().ToList();
            _parts = new ObservableCollection<SchemePartRow>(SchemePartRow.From(new NumberingScheme("", "", NumberingTemplate.Text.Trim(), false, TimeSpan.FromHours(24), true) { Fields = fields }));
            PartList.ItemsSource = _parts;
            PartList.SelectedIndex = _parts.Count > 0 ? 0 : -1;
            RefreshNumberingExample();
            ShowProblem(null);
        }
        catch (FormatException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    /// <summary>The scheme the parts make (template, fields, first number), or why they don't make one yet.</summary>
    private (string? Template, IReadOnlyList<NumberingField> Fields, long Start, string? Problem) BuildScheme()
    {
        var start = long.TryParse(NumberingStart.Text.Trim(), out var first) ? first : -1;
        var fields = new List<NumberingField>();
        foreach (var part in _parts.Where(p => p.IsField))
        {
            if (SchemeParts.FieldNameProblem(part.Name) is { } nameProblem)
                return (null, fields, start, nameProblem);
            if (fields.Any(f => string.Equals(f.Name, part.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return (null, fields, start, $"Two values are called {part.Name.Trim()}; give each its own name.");
            if (part.Type == SchemePartType.Typed && (part.MinLength.Trim().Length > 0 && SchemePartRow.Parse(part.MinLength) is not > 0
                || part.MaxLength.Trim().Length > 0 && SchemePartRow.Parse(part.MaxLength) is not > 0))
            {
                return (null, fields, start, $"The shortest and longest lengths of {part.Name.Trim()} must be whole numbers above 0.");
            }
            var field = part.ToField()!;
            if (field.MinLength > field.MaxLength)
                return (null, fields, start, $"The shortest length of {field.Name} is longer than its longest.");
            if (field.Default is { } defaultValue && field.Problem(defaultValue) is { } defaultProblem)
                return (null, fields, start, $"The default doesn't fit: {defaultProblem}");
            fields.Add(field);
        }
        if (start < 0)
            return (null, fields, start, "The first number must be a whole number, e.g. 1 or 100000.");
        var template = SchemeParts.ToTemplate(_parts.Select(p => p.ToTemplatePart()));
        try
        {
            NumberTemplate.Parse(template);
        }
        catch (FormatException ex)
        {
            return (template, fields, start, _parts.Count(p => p.Type == SchemePartType.Counter) switch
            {
                0 => "Add a Counter: every number needs one.",
                > 1 => "A number has one Counter; remove the others.",
                _ => ex.Message,
            });
        }
        return (template, fields, start, null);
    }

    /// <summary>Shows numbers the parts make and how the counter counts, and keeps the template text in step.</summary>
    private void RefreshNumberingExample()
    {
        if (_editingNumbering is null)
            return;
        var (template, fields, start, problem) = BuildScheme();
        if (template is not null)
            NumberingTemplate.Text = template;
        if (problem is not null)
        {
            NumberingExample.Text = problem;
            NumberingCounterNote.Text = "";
            return;
        }
        var scheme = _editingNumbering with { Template = template!, Fields = fields, StartAt = start };
        NumberingExample.Text = string.Join("    ", Enumerable.Range(0, 3).Select(i => NamingChoices.Example(scheme, i))) + "  …";
        var before = _parts.TakeWhile(p => p.Type != SchemePartType.Counter)
            .Where(p => p.IsField || p.Type is SchemePartType.Year or SchemePartType.Site)
            .Select(p => p.IsField ? p.Name.Trim() : p.TypeName.ToLowerInvariant()).ToList();
        NumberingCounterNote.Text = before.Count == 0
            ? $"One counter for the whole scheme, from {start}."
            : $"The counter starts again at {start} for each different {JoinAnd(before)}.";
    }

    private static string JoinAnd(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    private void AddNumbering_Click(object sender, RoutedEventArgs e)
    {
        var scheme = new NumberingScheme(AdminState.NewId("new numbering", _numbering.Select(n => n.Id)), "New numbering", "{seq:00000}", false, TimeSpan.FromHours(24), true);
        _numbering.Add(scheme);
        NumberingList.ItemsSource = null;
        NumberingList.ItemsSource = _numbering;
        NumberingList.SelectedItem = scheme;
    }

    /// <summary>Deletes the selected scheme: from the list if it was never saved, else on the server, which refuses one that is in use.</summary>
    private async void DeleteNumbering_Click(object sender, RoutedEventArgs e)
    {
        if (NumberingList.SelectedItem is not NumberingScheme scheme)
            return;
        if (scheme.InUse)
        {
            ShowProblem($"{scheme.Name} has issued numbers, so it stays to explain them. Untick In use to stop it issuing more.");
            return;
        }
        try
        {
            if ((await App.Client.ListNumberingSchemesAsync()).Any(s => s.Id == scheme.Id))
                await App.Client.DeleteNumberingSchemeAsync(scheme.Id);
            _numbering.Remove(scheme);
            NumberingList.ItemsSource = null;
            NumberingList.ItemsSource = _numbering;
            NumberingList.SelectedIndex = _numbering.Count > 0 ? 0 : -1;
            RefreshChoices();
            ShowProblem(null, $"Deleted the {scheme.Name} numbering scheme.");
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem($"{scheme.Name} wasn't deleted: {ex.Message}");
        }
    }

    /// <summary>Offers the starter schemes; the one picked is added as an unsaved copy to make your own.</summary>
    private void AddNumberingFromStarter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddFromStarter, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var starter in StarterSchemes.All)
        {
            var item = new MenuItem { Header = $"{starter.Name}   e.g. {NamingChoices.Example(starter)}", ToolTip = starter.Description };
            item.Click += (_, _) =>
            {
                var copy = starter with { Id = AdminState.NewId(starter.Id, _numbering.Select(n => n.Id)), InUse = false };
                _numbering.Add(copy);
                NumberingList.ItemsSource = null;
                NumberingList.ItemsSource = _numbering;
                NumberingList.SelectedItem = copy;
                ShowProblem(null, $"Added {starter.Name}. Adjust it to suit (codes, first number, what fills in), then Save This Scheme.");
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>Shows how a new file saved in the given folder would be named, using the rules as edited (saved or not).</summary>
    private async void TryNaming_Click(object sender, RoutedEventArgs e)
    {
        var folder = NamingTryFolder.Text.Trim().TrimEnd('/');
        var type = NamingTryType.Text.Trim();
        var fileName = "new" + (type.StartsWith('.') ? type : "." + type);
        var parent = NamingTryParent.Text.Trim();
        if (!folder.StartsWith('$'))
        {
            NamingTryResult.Text = "Give a vault folder, e.g. $/Models/PRJ01 - Pump skid/02 - Structure.";
            return;
        }
        var suggestion = NamingRules.Suggest(_state.ToConfiguration(), _numbering, folder, fileName, parent.Length > 0 ? parent : null);
        var lines = new List<string>
        {
            suggestion.CategoryName is { } category ? $"Category: {category}" : "No category matches: the file gets no category and is named freely.",
        };
        if (suggestion.Scheme is not { } scheme)
        {
            if (suggestion.CategoryName is not null)
                lines.Add("This category has no numbering scheme, so the file is named freely.");
            NamingTryResult.Text = string.Join(Environment.NewLine, lines);
            return;
        }
        lines.Add($"Numbering scheme: {scheme.Name}   (e.g. {NamingChoices.Example(scheme)})");
        foreach (var field in suggestion.Fields)
        {
            lines.Add(field.Value is { } value
                ? $"    {field.Label} = {value}   ({field.Source switch { NumberingSources.Folder => "from the folders", NumberingSources.Parent => "from the assembly", "default" => "default", _ => "" }})"
                : $"    {field.Label}: still to choose ({NamingRules.Describe(field.Field)})");
        }
        if (suggestion.Complete)
        {
            try
            {
                var next = await App.Client.PreviewNumberAsync(scheme.Id, suggestion.Fields.ToDictionary(f => f.Field.Name, f => f.Value!));
                lines.Add($"Next number: {next.Next}   ({next.Counter})");
            }
            catch (OpenholdApiException ex)
            {
                // A scheme added here but not yet saved isn't on the server, so it has no next number yet.
                lines.Add(ex.StatusCode == System.Net.HttpStatusCode.NotFound ? "Save the scheme to see the next number." : $"Next number: {ex.Message}");
            }
        }
        lines.Add(suggestion.Enforced ? "Names are required to follow this scheme." : "Following the scheme is suggested, not required.");
        NamingTryResult.Text = string.Join(Environment.NewLine, lines);
    }

    private async void SaveNumbering_Click(object sender, RoutedEventArgs e)
    {
        if (_editingNumbering is not { } scheme)
            return;
        var (template, fields, start, problem) = BuildScheme();
        if (problem is not null)
        {
            ShowProblem(problem);
            return;
        }
        var changed = scheme with
        {
            Name = NumberingName.Text.Trim(),
            Template = template!,
            AllowReuse = NumberingReuse.IsChecked == true,
            Enabled = NumberingEnabled.IsChecked == true,
            StartAt = start,
            Description = NumberingDescription.Text.Trim(),
            Fields = fields,
        };
        try
        {
            var saved = await App.Client.SaveNumberingSchemeAsync(changed);
            _numbering[_numbering.IndexOf(scheme)] = saved;
            NumberingList.ItemsSource = null;
            NumberingList.ItemsSource = _numbering;
            NumberingList.SelectedItem = saved;
            RefreshChoices();
            ShowProblem(null, $"Saved the {saved.Name} numbering scheme.");
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    // ---- Jobs ----

    private async Task LoadJobsAsync()
    {
        var status = (JobFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        Jobs.ItemsSource = (await App.Client.ListJobsAsync(string.IsNullOrEmpty(status) ? null : status))
            .Select(j => j with { CreatedAt = j.CreatedAt.ToLocalTime() }).ToList();
    }

    private async void JobFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            await LoadJobsAsync();
    }

    private async void RefreshJobs_Click(object sender, RoutedEventArgs e) => await LoadJobsAsync();

    // ---- Item classes (part of the configuration, saved with Save Changes) ----

    public ObservableCollection<ItemClassModel> ItemClasses => _state.ItemClasses;

    /// <summary>Classes a class can sit inside, with "(top level)" first.</summary>
    public ObservableCollection<Choice> ItemClassChoices { get; } = [];

    private void AddItemClass_Click(object sender, RoutedEventArgs e)
    {
        var parent = ItemClassList.SelectedItem as ItemClassModel;
        var itemClass = new ItemClassModel { Id = AdminState.NewId("new class", ItemClasses.Select(c => c.Id)), Name = "New class", ParentId = parent?.Id ?? "" };
        ItemClasses.Insert(parent is null ? ItemClasses.Count : ItemClasses.IndexOf(parent) + 1, itemClass);
        RefreshChoices();
        ItemClassList.SelectedItem = itemClass;
    }

    private void RemoveItemClass_Click(object sender, RoutedEventArgs e)
    {
        if (ItemClassList.SelectedItem is not ItemClassModel itemClass)
            return;
        if (ItemClasses.FirstOrDefault(c => c.ParentId == itemClass.Id) is { } child)
        {
            ShowProblem($"{child.Name} is inside {itemClass.Name}. Move or remove it first.");
            return;
        }
        ItemClasses.Remove(itemClass);
        RefreshChoices();
    }

    // ---- ERP connections (saved one at a time through their own endpoints) ----

    private IReadOnlyList<ErpKindInfo> _erpKinds = [];
    private ErpConnectionDetails? _erpEditing;   // null while adding a new connection

    private sealed record ErpRow(ErpConnectionDetails Details, string Name, string Summary);

    /// <summary>One field of a connection: text, or a secret typed into a password box (left empty, the saved one stays).</summary>
    private sealed class ErpField(ErpSettingInfo setting, string value, bool saved)
    {
        public ErpSettingInfo Setting { get; } = setting;
        public string Label { get; } = setting.Label + (setting.Required ? "" : " (optional)");
        public string? Hint { get; } = setting.Secret && saved ? "Saved. Type a new one to replace it." : setting.Hint;
        public string Value { get; set; } = value;
        public bool Typed { get; set; }
        public Visibility TextVisibility => Setting.Secret ? Visibility.Collapsed : Visibility.Visible;
        public Visibility SecretVisibility => Setting.Secret ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadErpAsync(string? select = null)
    {
        try
        {
            _erpKinds = await App.Client.ListErpKindsAsync();
            var status = (await App.Client.ListErpConnectionsAsync()).ToDictionary(c => c.Id);
            var rows = (await App.Client.ListErpSetupAsync()).Select(d => new ErpRow(d, d.Name,
                $"{_erpKinds.FirstOrDefault(k => k.Kind == d.Kind)?.Name ?? d.Kind}" + (d.Enabled ? "" : ", switched off") +
                (status.TryGetValue(d.Id, out var s) && s.Failed > 0 ? $", {s.Failed} failed" : ""))).ToList();
            ErpList.ItemsSource = rows;
            ErpKind.ItemsSource = _erpKinds;
            ErpList.SelectedItem = rows.FirstOrDefault(r => r.Details.Id == select) ?? rows.FirstOrDefault();
        }
        catch (OpenholdApiException)
        {
            // Needs Manage Configuration; the tab stays empty otherwise.
        }
    }

    private void ErpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ErpList.SelectedItem is ErpRow row)
            ShowErp(row.Details);
    }

    private void NewErpConnection_Click(object sender, RoutedEventArgs e)
    {
        ErpList.SelectedItem = null;
        ShowErp(null);
        ErpName.Focus();
    }

    private void ShowErp(ErpConnectionDetails? details)
    {
        _erpEditing = details;
        ErpEditor.IsEnabled = true;
        var readOnly = details?.FromServerSettings == true;
        ErpFromFile.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        ErpName.Text = details?.Name ?? "";
        ErpId.Text = details?.Id ?? "";
        ErpId.IsReadOnly = details is not null;
        ErpEnabled.IsChecked = details?.Enabled ?? true;
        ErpKind.SelectedItem = _erpKinds.FirstOrDefault(k => k.Kind == details?.Kind) ?? _erpKinds.FirstOrDefault();
        ErpKind.IsEnabled = details is null;
        ErpSave.IsEnabled = !readOnly;
        ErpRemove.IsEnabled = details is not null && !readOnly;
        ShowErpFields();
        ErpStatusText.Text = details is null ? "New connection: fill it in, Save, then Test." : "";
        if (details is not null)
            _ = ShowErpStatusAsync(details.Id);
    }

    private async Task ShowErpStatusAsync(string id)
    {
        try
        {
            var status = (await App.Client.ListErpConnectionsAsync()).FirstOrDefault(c => c.Id == id);
            ErpStatusText.Text = status is null ? "" : $"{status.Sent} item(s) up to date, {status.Pending} waiting to be sent, {status.Failed} failed (failures are retried automatically; Test shows why).";
        }
        catch (OpenholdApiException)
        {
            ErpStatusText.Text = "";
        }
    }

    private void ErpKind_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowErpFields();

    private void ShowErpFields()
    {
        var kind = ErpKind.SelectedItem as ErpKindInfo;
        ErpKindDescription.Text = kind?.Description ?? "";
        ErpFields.ItemsSource = (kind?.Settings ?? []).Select(s => new ErpField(s,
            _erpEditing?.Settings.GetValueOrDefault(s.Name) ?? "", _erpEditing?.SecretsSaved.Contains(s.Name) == true)).ToList();
    }

    private void ErpSecret_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ErpField field } box)
        {
            field.Value = box.Password;
            field.Typed = true;
        }
    }

    private async void SaveErpConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ErpKind.SelectedItem is not ErpKindInfo kind)
            return;
        var fields = ErpFields.ItemsSource as IEnumerable<ErpField> ?? [];
        var id = ErpId.Text.Trim().Length > 0 ? ErpId.Text.Trim() : AdminState.NewId(ErpName.Text, []);
        try
        {
            var saved = await App.Client.SaveErpConnectionAsync(id, new SaveErpConnectionRequest(ErpName.Text.Trim(), kind.Kind, ErpEnabled.IsChecked == true,
                fields.Where(f => !f.Setting.Secret).ToDictionary(f => f.Setting.Name, f => f.Value),
                fields.Where(f => f.Setting.Secret && f.Typed).ToDictionary(f => f.Setting.Name, f => f.Value)));
            ShowProblem(null, $"Saved {saved.Name}. Click Test to check it can reach {kind.Name}.");
            await LoadErpAsync(saved.Id);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    private async void TestErpConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_erpEditing is not { } connection)
        {
            ShowProblem("Save the connection first, then test it.");
            return;
        }
        try
        {
            await App.Client.TestErpConnectionAsync(connection.Id);
            ShowProblem(null, $"{connection.Name} works: Openhold signed in and read from it without changing anything.");
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    private async void SendAllToErp_Click(object sender, RoutedEventArgs e)
    {
        if (_erpEditing is not { } connection
            || MessageBox.Show(this, $"Send every released and obsolete item to {connection.Name}? Items are sent in the background over the next few minutes.",
                "Openhold: ERP", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            var queued = await App.Client.SendAllToErpAsync(connection.Id);
            ShowProblem(null, $"{queued} item(s) queued for {connection.Name}.");
            await ShowErpStatusAsync(connection.Id);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    private async void RemoveErpConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_erpEditing is not { } connection
            || MessageBox.Show(this, $"Remove {connection.Name}? Openhold stops sending to it. What was already sent stays in the ERP.",
                "Openhold: ERP", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await App.Client.DeleteErpConnectionAsync(connection.Id);
            ShowProblem(null, $"Removed {connection.Name}.");
            await LoadErpAsync();
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    // ---- Folder permissions (part of the configuration, saved with Save Changes) ----

    public ObservableCollection<FolderRuleModel> FolderRules => _state.FolderRules;

    /// <summary>Groups a rule can name, with "Everyone" first.</summary>
    public ObservableCollection<Choice> GroupChoices { get; } = [];

    public IReadOnlyList<AccessChoice> AccessChoices { get; } =
        [new(FolderAccess.Change, "Change"), new(FolderAccess.Read, "Read only"), new(FolderAccess.None, "Hidden")];

    public sealed record AccessChoice(FolderAccess Value, string Name);

    private void DefaultAccess_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultAccess.SelectedItem is ComboBoxItem { Tag: string tag })
            _state.DefaultFolderAccess = Enum.Parse<FolderAccess>(tag);
    }

    private void AddFolderRule_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPickerWindow("Set permissions on:") { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedVaultPath is not { } folder)
            return;
        var rule = new FolderRuleModel { Path = folder, Group = "everyone", Access = FolderAccess.Read };
        FolderRules.Add(rule);
        FolderRulesGrid.SelectedItem = rule;
        FolderRulesGrid.ScrollIntoView(rule);
        ExplainFolder.Text = folder;
    }

    private void RemoveFolderRule_Click(object sender, RoutedEventArgs e)
    {
        if (FolderRulesGrid.SelectedItem is FolderRuleModel rule)
            FolderRules.Remove(rule);
    }

    /// <summary>What a person may do in a folder under the rules as they stand in this window, and which rule decides.</summary>
    private void Explain_Click(object sender, RoutedEventArgs e)
    {
        var person = (ExplainPerson.SelectedItem as UserInfo)?.Username ?? ExplainPerson.Text.Trim();
        var folder = ExplainFolder.Text.Trim();
        if (person.Length == 0 || !folder.StartsWith('$'))
        {
            ExplainResult.Text = "Choose a person and type a vault folder, e.g. $/Projects/PRJ01.";
            return;
        }
        try
        {
            var (access, reason) = _state.ToConfiguration().ExplainAccess(person, folder.TrimEnd('/'));
            var what = access switch
            {
                FolderAccess.Change => "can see and change files there",
                FolderAccess.Read => "can see and get files there, but not change them",
                _ => "can't see that folder at all",
            };
            ExplainResult.Text = $"{person} {what}. {reason}" + (HasUnsavedChanges() ? " (Includes changes not saved yet.)" : "");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            ExplainResult.Text = $"Something isn't filled in correctly: {ex.Message}";
        }
    }

    // ---- Sites ----

    private sealed record SiteRow(string Id, string Name, string Role, int Missing, string LastSeen, string? Url);

    private async Task LoadSitesAsync()
    {
        var sites = await App.Client.ListSitesAsync();
        Sites.ItemsSource = sites.Select(s => new SiteRow(s.Id, s.Name,
            (s.IsAuthority ? "Lock server (authority)" : "Office server") + (s.IsThisSite ? ", this site" : ""),
            s.Missing,
            s.LastSeen?.ToLocalTime().ToString("d MMM yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture) ?? "Never",
            s.Url)).ToList();
    }

    private async void RefreshSites_Click(object sender, RoutedEventArgs e) => await LoadSitesAsync();

    private async void CatchUp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var arrived = await App.Client.CatchUpSiteAsync();
            ShowProblem(null, arrived == 0 ? "This site already has every file." : $"Fetched {arrived} file version(s) from the other sites.");
            await LoadSitesAsync();
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    // ---- Audit log (needs View Audit) ----

    private async void ShowAudit_Click(object sender, RoutedEventArgs e)
    {
        var days = int.Parse((AuditDays.SelectedItem as ComboBoxItem)?.Tag as string ?? "7", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Audit.ItemsSource = (await App.Client.ListAuditAsync(DateTime.UtcNow.AddDays(-days), null,
                    AuditPerson.Text.Trim() is { Length: > 0 } person ? person : null,
                    AuditAction.Text.Trim() is { Length: > 0 } action ? action : null, 2000))
                .Select(a => a with { At = a.At.ToLocalTime() }).ToList();
            ShowProblem(null);
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
    }

    private async void ExportAccessReview_Click(object sender, RoutedEventArgs e)
    {
        var save = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Openhold access review {DateTime.Now:yyyy-MM-dd}.csv",
            Filter = "CSV file (*.csv)|*.csv",
        };
        if (save.ShowDialog(this) != true)
            return;
        try
        {
            var review = await App.Client.GetAccessReviewAsync();
            static string Cell(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
            var lines = new List<string> { "Username,Name,Signs in with,Disabled,Two-factor,Last signed in,Groups,Privileges" };
            lines.AddRange(review.Select(r => string.Join(',', Cell(r.Username), Cell(r.DisplayName), Cell(r.Kind), r.Disabled ? "Yes" : "No",
                r.TwoFactorEnabled ? "On" : "Off", r.LastSignInAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "Never",
                Cell(string.Join("; ", r.Groups)), Cell(string.Join("; ", r.Privileges)))));
            await System.IO.File.WriteAllLinesAsync(save.FileName, lines, new System.Text.UTF8Encoding(true));
            ShowProblem(null, $"Saved the access review for {review.Count} people to {save.FileName}.");
        }
        catch (Exception ex) when (ex is OpenholdApiException or System.IO.IOException or UnauthorizedAccessException)
        {
            ShowProblem(ex.Message);
        }
    }

    // ---- History ----

    private async void LoadVersion_Click(object sender, RoutedEventArgs e)
    {
        if (History.SelectedItem is not ConfigurationVersion version)
            return;
        var old = await App.Client.GetConfigurationVersionAsync(version.Version);
        Show(old.Configuration);
        Comment.Text = $"Back to version {version.Version}";
        ShowProblem(null, $"Version {version.Version} is in the editor. Check it, then Save Changes to make it the newest version.");
        Tabs.SelectedIndex = 0;
    }

    // ---- Saving ----

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        VaultConfiguration configuration;
        try
        {
            configuration = _state.ToConfiguration();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            ShowProblem($"Something isn't filled in correctly: {ex.Message}");
            return;
        }
        if (configuration.Validate() is { Count: > 0 } problems)
        {
            ShowProblem("Not saved yet: " + string.Join(" ", problems));
            return;
        }
        if (!HasUnsavedChanges())
        {
            ShowProblem(null, "Nothing has changed.");
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var comment = Comment.Text.Trim().Length > 0 ? Comment.Text.Trim() : "Changed in Openhold Explorer";
            var saved = await App.Client.SaveConfigurationAsync(new SaveConfigurationRequest(_basedOnVersion, configuration, comment));
            Saved = true;
            App.Configuration = saved.Configuration;
            _basedOnVersion = saved.Version;
            _savedJson = Baseline(saved.Configuration);
            Comment.Clear();
            History.ItemsSource = await App.Client.GetConfigurationHistoryAsync();
            ShowProblem(null, $"Saved as version {saved.Version}. Everyone gets it the next time Openhold refreshes.");
        }
        catch (OpenholdApiException ex)
        {
            ShowProblem(ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowProblem(string? problem, string? note = null)
    {
        Problem.Text = problem ?? note ?? "";
        Problem.Foreground = (System.Windows.Media.Brush)FindResource(problem is null ? "Brand" : "DangerText");
        Problem.Visibility = Problem.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
