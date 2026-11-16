using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KosGovernor.Core;

namespace KosGovernor.Views;

/// <summary>An open program offered in <see cref="AddRulesDialog"/>.</summary>
public sealed class OpenProgram
{
    public string Name { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public bool IsGame { get; init; }
    public bool IsSelected { get; set; }
}

/// <summary>Rules for several open programs at once: the ticked ones get the chosen cores (CPU sets for games, affinity for the rest).</summary>
public partial class AddRulesDialog : Window
{
    AddRulesDialog() => InitializeComponent();

    /// <summary>The ticked programs, the cores (a <see cref="CpuSpec"/>) and whether the programs they open get them too; null = cancelled.</summary>
    public static (List<OpenProgram> Programs, string Cores, bool Children)? Pick(IReadOnlyList<OpenProgram> programs, GovernorConfig config)
    {
        var d = new AddRulesDialog();
        var topo = CpuTopology.Current;
        var cores = CpuSpec.Presets(topo).Select(p => new Opt(p.Label, p.Spec)).Append(new Opt("All", CpuSpec.All))
            .Concat(config.Masks.Select(m => new Opt($"Mask: {m.Name}", CpuSpec.MaskPrefix + m.Name))).ToList();
        d.Programs.ItemsSource = programs;
        d.CoresBox.ItemsSource = cores;
        // The V-Cache CCD or the P-cores, when the CPU has them: what a game usually wants.
        d.CoresBox.SelectedItem = cores.FirstOrDefault(o => (string?)o.Value == CpuSpec.VCache) ?? cores.FirstOrDefault(o => (string?)o.Value == CpuSpec.PCores) ?? cores[0];
        DialogSetup.Prepare(d, d.Root);
        d.Loaded += (_, _) => d.Validate();
        if (d.ShowDialog() != true) return null;
        return (programs.Where(p => p.IsSelected).ToList(), (string)((Opt)d.CoresBox.SelectedItem).Value!, d.ChildrenBox.IsChecked == true);
    }

    bool Validate()
    {
        var none = ((IEnumerable<OpenProgram>)Programs.ItemsSource).All(p => !p.IsSelected);
        Error.Text = none ? "Tick at least one program." : "";
        Error.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        Ok.IsEnabled = !none;
        return !none;
    }

    void OnTicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: OpenProgram p } box) p.IsSelected = box.IsChecked == true; // the binding may update after this event
        Validate();
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (Validate()) DialogResult = true;
    }
}
