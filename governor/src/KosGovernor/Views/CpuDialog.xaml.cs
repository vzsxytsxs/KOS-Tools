using System.Windows;
using System.Windows.Controls;
using KosGovernor.Core;

namespace KosGovernor.Views;

/// <summary>Choice of logical processors (affinity, CPU sets, masks) grouped by core.</summary>
public partial class CpuDialog : Window
{
    readonly CpuTopology _topo = CpuTopology.Current;
    readonly Dictionary<int, CheckBox> _boxes = new();
    GovernorConfig _config = new();
    IReadOnlyList<string> _names = Array.Empty<string>();
    bool _askName;

    CpuDialog() => InitializeComponent();

    /// <summary>Chosen mask (never empty) and the name, when <paramref name="askName"/>.</summary>
    public static (bool Ok, ulong Mask, string Name) Pick(string title, ulong initial, GovernorConfig config, string name = "", bool askName = false)
    {
        var d = new CpuDialog { Title = title, _config = config, _askName = askName };
        d.Heading.Text = title;
        d._names = config.Masks.Select(m => m.Name).Where(n => n != name).ToList();
        d.NameRow.Visibility = askName ? Visibility.Visible : Visibility.Collapsed;
        d.NameBox.Text = name;
        var coreIndex = 0;
        var secondary = (System.Windows.Media.Brush)d.FindResource("TextSecondaryBrush");
        foreach (var (groupTitle, cores) in d._topo.Groups())
        {
            if (groupTitle.Length > 0) d.Cores.Children.Add(new TextBlock { Text = groupTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            var row = new WrapPanel();
            foreach (var core in cores)
            {
                var block = new StackPanel { Margin = new Thickness(0, 0, 14, 12), MinWidth = 40 };
                block.Children.Add(new TextBlock { Text = coreIndex++.ToString(), FontSize = 10.5, Foreground = secondary, Margin = new Thickness(0, 0, 0, 4) });
                foreach (var cpu in core)
                {
                    var box = new CheckBox { Content = cpu.Index.ToString(), Margin = new Thickness(0, 0, 0, 6), IsChecked = (initial >> cpu.Index & 1) == 1, FontSize = 12 };
                    box.Checked += (_, _) => d.Validate();
                    box.Unchecked += (_, _) => d.Validate();
                    d._boxes[cpu.Index] = box;
                    block.Children.Add(box);
                }
                row.Children.Add(block);
            }
            d.Cores.Children.Add(row);
        }
        DialogSetup.Prepare(d, d.Root);
        d.Loaded += (_, _) => { d.Validate(); if (askName) { d.NameBox.Focus(); d.NameBox.SelectAll(); } };
        return d.ShowDialog() == true ? (true, d.Mask, d.NameBox.Text.Trim()) : (false, 0, "");
    }

    ulong Mask => _boxes.Where(b => b.Value.IsChecked == true).Aggregate(0UL, (m, b) => m | 1UL << b.Key);

    void Set(ulong mask)
    {
        foreach (var (i, box) in _boxes) box.IsChecked = (mask >> i & 1) == 1;
    }

    void Validate()
    {
        var name = NameBox.Text.Trim();
        var error = Mask == 0 ? "Pick at least one processor."
            : !_askName ? null
            : name.Length is 0 or > 40 ? "Give the mask a name (up to 40 characters)."
            : name.Contains(':') ? "The name can't have \":\"."
            : _names.Contains(name, StringComparer.OrdinalIgnoreCase) ? "There's already a mask with this name." : null;
        Error.Text = error ?? "";
        Error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        Ok.IsEnabled = error is null;
    }

    void OnChanged(object sender, TextChangedEventArgs e) => Validate();
    void OnNone(object sender, RoutedEventArgs e) => Set(0);
    void OnAll(object sender, RoutedEventArgs e) => Set(_topo.AllMask);

    void OnPreset(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string label, ulong mask) { var item = new MenuItem { Header = label }; item.Click += (_, _) => Set(mask); menu.Items.Add(item); }
        foreach (var (label, spec) in CpuSpec.Presets(_topo))
            if (CpuSpec.Resolve(spec, _topo, _config.Masks, out _) is { } preset) Add(label, preset);
        foreach (var m in _config.Masks)
            if (CpuSpec.ParseList(m.Cpus, _topo, out _) is { } mask) Add(m.Name, mask);
        if (menu.Items.Count == 0) Add("All", _topo.AllMask);
        menu.IsOpen = true;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (Ok.IsEnabled) DialogResult = true;
    }
}
