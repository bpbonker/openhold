using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Openhold.Desktop;

/// <summary>A folder in the tree. Children load the first time it is expanded.</summary>
public sealed class FolderNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _loaded;

    public FolderNode(string name, string vaultPath, FolderNode? parent)
    {
        Name = name;
        VaultPath = vaultPath;
        Parent = parent;
        // A placeholder child makes the expand arrow show until the real children are loaded.
        if (parent is not null || vaultPath == "$")
            Children.Add(new FolderNode("Loading…", "", null));
    }

    public string Name { get; }

    public string VaultPath { get; }

    public FolderNode? Parent { get; }

    public ObservableCollection<FolderNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged();
            // Fire-and-forget is safe here: LoadChildrenAsync reports its own failures in the tree.
            if (value && !_loaded)
                _ = LoadChildrenAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadChildrenAsync()
    {
        _loaded = true;
        try
        {
            var folder = await App.Workspace.GetFolderAsync(VaultPath);
            Children.Clear();
            foreach (var name in folder.Subfolders)
                Children.Add(new FolderNode(name, $"{VaultPath}/{name}", this));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Openhold.Client.Sdk.OpenholdApiException or System.IO.IOException)
        {
            // Leave the folder retryable rather than stuck on "Loading…".
            _loaded = false;
            Children.Clear();
            Children.Add(new FolderNode("Couldn't load. Press Refresh to try again.", "", null));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>What screen readers and UI automation announce for the folder.</summary>
    public override string ToString() => Name;
}
