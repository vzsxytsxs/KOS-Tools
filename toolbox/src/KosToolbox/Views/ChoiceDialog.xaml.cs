using System.ComponentModel;
using System.Windows;
using KosToolbox.ViewModels;

namespace KosToolbox.Views;

/// <summary>A checkable option of the choice dialog.</summary>
public sealed class ChoiceItem(string group, string label, string? detail, string? icon, object tag) : ObservableObject
{
    bool _checked;
    public string Group { get; } = group;
    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public string? Icon { get; } = icon;
    public object Tag { get; } = tag;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

public sealed record ChoiceGroup(string Title, IReadOnlyList<ChoiceItem> Items);

/// <summary>Dialog with checkable options in groups (e.g. Discord versions); confirming requires at least one.</summary>
public partial class ChoiceDialog : Window
{
    ChoiceDialog() => InitializeComponent();

    /// <summary>Shows the options (their checked state changes directly on the items); true if confirmed.</summary>
    public static bool Choose(string title, string message, IReadOnlyList<ChoiceItem> items, string okText, bool single)
    {
        var d = new ChoiceDialog { Title = title };
        d.Heading.Text = title;
        d.Body.Text = message;
        d.Groups.ItemsSource = items.GroupBy(i => i.Group).Select(g => new ChoiceGroup(g.Key, g.ToList())).ToList();
        d.Ok.Content = okText;
        void Update() => d.Ok.IsEnabled = items.Any(i => i.IsChecked);
        foreach (var item in items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ChoiceItem.IsChecked)) return;
                // Single choice: checking one unchecks the others.
                if (single && item.IsChecked) foreach (var other in items) if (other != item) other.IsChecked = false;
                Update();
            };
        Update();
        EditorDialog.SetOwner(d);
        d.SourceInitialized += (_, _) => DarkTitleBar.Apply(d);
        d.Loaded += (_, _) => Motion.Pop(d.Root);
        return d.ShowDialog() == true;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (Ok.IsEnabled) DialogResult = true;
    }
}
