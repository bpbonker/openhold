using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Openhold.Client.Sdk;
using Openhold.Core.Contracts;

namespace Openhold.Desktop;

/// <summary>
/// Asks for a new number, like a numbering spreadsheet: choose a scheme, pick its fields from lists (or type them),
/// see the next number as you go, and reserve it. A reserved number is held for the person and becomes theirs for
/// good when a file with that name is checked in. Schemes that number catalogue items aren't offered here: those
/// numbers come from adding the item in the Catalogue.
/// </summary>
public partial class NewNumberWindow : Window
{
    private readonly DispatcherTimer _previewDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly string? _parent;
    private ReservedNumber? _reserved;

    /// <param name="parent">Filled in as the Parent field (child numbering), e.g. the number of the selected assembly.</param>
    public NewNumberWindow(string? parent = null)
    {
        InitializeComponent();
        _parent = parent;
        _previewDelay.Tick += async (_, _) =>
        {
            _previewDelay.Stop();
            await PreviewAsync();
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    /// <summary>The number reserved here, if any.</summary>
    public string? ReservedNumber => _reserved?.Formatted;

    private async Task LoadAsync()
    {
        try
        {
            var schemes = (await App.Client.ListNumberingSchemesAsync())
                .Where(s => s.Enabled && !App.Configuration.NumbersCatalogueItems(s.Id))
                .ToList();
            Schemes.ItemsSource = schemes;
            Schemes.SelectedItem = schemes.FirstOrDefault(s => _parent is not null && s.Fields.Any(f => IsParent(f.Name))) ?? schemes.FirstOrDefault();
            if (schemes.Count == 0)
                Show("No numbering schemes yet", "An administrator sets them up in Administration › Numbering.", null);
        }
        catch (Exception ex) when (ex is OpenholdApiException or System.Net.Http.HttpRequestException)
        {
            Show("Couldn't load the numbering schemes", ex.Message, null);
        }
    }

    private static bool IsParent(string field) => string.Equals(field, "Parent", StringComparison.OrdinalIgnoreCase);

    private void Schemes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var scheme = Schemes.SelectedItem as NumberingScheme;
        SchemeDescription.Text = scheme?.Description ?? "";
        var names = scheme is null ? [] : Openhold.Core.Numbering.NumberTemplate.Parse(scheme.Template).FieldNames;
        Fields.ItemsSource = names.Select(name =>
        {
            var definition = scheme!.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return new FieldRow(name, definition?.Label ?? name, definition?.Choices) { Value = IsParent(name) ? _parent ?? "" : "" };
        }).ToList();
        _reserved = null;
        CopyButton.IsEnabled = false;
        ReserveButton.IsEnabled = scheme is not null;
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_reserved is not null)
            return; // keep showing what was reserved until the scheme changes
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private IReadOnlyDictionary<string, string> Values() =>
        (Fields.ItemsSource as IEnumerable<FieldRow> ?? []).ToDictionary(f => f.Name, f => f.Value?.Trim() ?? "");

    private async Task PreviewAsync()
    {
        if (Schemes.SelectedItem is not NumberingScheme scheme)
            return;
        var missing = (Fields.ItemsSource as IEnumerable<FieldRow> ?? []).FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Value));
        if (missing is not null)
        {
            Show("Next number", $"Fill in {missing.Label} to see the next number.", null);
            return;
        }
        try
        {
            var preview = await App.Client.PreviewNumberAsync(scheme.Id, Values());
            Show("Next number", preview.Next, $"Not reserved yet; someone else could take it first. ({preview.Counter})");
        }
        catch (OpenholdApiException ex)
        {
            Show("Next number", "", ex.Message);
        }
    }

    private async void Reserve_Click(object sender, RoutedEventArgs e)
    {
        if (Schemes.SelectedItem is not NumberingScheme scheme)
            return;
        ReserveButton.IsEnabled = false;
        try
        {
            _reserved = (await App.Client.ReserveNumbersAsync(scheme.Id, 1, Values())).Single();
            Show("Reserved for you", _reserved.Formatted,
                $"Held until {_reserved.ExpiresAt.ToLocalTime():d MMM h:mm tt}. Save your file as {_reserved.Formatted} and check it in to keep the number; " +
                "if you don't, it's freed again.");
            CopyButton.IsEnabled = true;
            Clipboard.SetText(_reserved.Formatted);
        }
        catch (OpenholdApiException ex)
        {
            Show("Next number", "", ex.Message);
        }
        finally
        {
            ReserveButton.IsEnabled = Schemes.SelectedItem is not null;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_reserved is not null)
            Clipboard.SetText(_reserved.Formatted);
    }

    private void Show(string label, string number, string? note)
    {
        ResultLabel.Text = label;
        Result.Text = number;
        ResultNote.Text = note ?? "";
    }

    /// <summary>One field of the scheme: a pick list when the scheme lists codes, otherwise typed.</summary>
    private sealed class FieldRow(string name, string label, IReadOnlyList<NumberingChoice>? choices) : INotifyPropertyChanged
    {
        public string Name { get; } = name;

        public string Label { get; } = label;

        public IReadOnlyList<Choice>? Choices { get; } = choices is { Count: > 0 } ? choices.Select(c => new Choice(c.Code, $"{c.Code}  —  {c.Meaning}")).ToList() : null;

        public Visibility ChoiceVisibility => Choices is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility TextVisibility => Choices is null ? Visibility.Visible : Visibility.Collapsed;

        public string? Value
        {
            get;
            set
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record Choice(string Code, string Display);
}
