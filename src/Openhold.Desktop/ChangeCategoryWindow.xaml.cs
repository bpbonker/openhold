using System.Windows;
using System.Windows.Controls;
using Openhold.Core.Configuration;

namespace Openhold.Desktop;

/// <summary>Change Category: picks the category (and so the lifecycle, revision scheme and numbering) for files.</summary>
public partial class ChangeCategoryWindow : Window
{
    private readonly VaultConfiguration _configuration;

    public ChangeCategoryWindow(int fileCount, IReadOnlyCollection<string?> currentCategories, VaultConfiguration configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        Intro.Text = $"Choose a category for {(fileCount == 1 ? "this file" : $"these {fileCount} files")}. " +
                     "The category decides the lifecycle, revision scheme and numbering scheme.";
        Categories.ItemsSource = configuration.Categories
            .Where(c => !(currentCategories.Count == 1 && string.Equals(currentCategories.First(), c.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public CategoryDefinition? Chosen => Categories.SelectedItem as CategoryDefinition;

    private void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Apply.IsEnabled = Chosen is not null;
        if (Chosen is not { } category)
            return;
        var lifecycle = _configuration.Lifecycle(category.LifecycleId);
        var revisions = _configuration.RevisionScheme(category.RevisionSchemeId);
        Explanation.Text =
            (lifecycle is null ? "No lifecycle: files can always be edited. " : $"Lifecycle: {lifecycle.Name}; the files will start at {lifecycle.Initial.Name}. ") +
            (revisions is null ? "" : $"Revisions: {revisions.Name}. ") +
            (category.NumberingSchemeId is { } numbering ? $"Numbering: {numbering}. " : "") +
            (category.Library ? "Library parts are shared rather than copied by Copy Design." : "");
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
