using System.Windows;
using System.Windows.Controls;
using KosToolbox.Core.Text;

namespace KosToolbox.Views;

public enum DialogChoice { Primary, Cancel }

/// <summary>Dark confirmation/warning/input dialog with selectable text.</summary>
public partial class Dialog : Window
{
    Func<string, string?>? _validate;
    DialogChoice _choice = DialogChoice.Cancel;

    Dialog() => InitializeComponent();

    public static bool Confirm(string title, string message, string confirmText) =>
        Open(Create(title, message, confirmText, cancellable: true)) == DialogChoice.Primary;

    public static void Show(string title, string message) => Open(Create(title, message, Txt.Ok, cancellable: false));

    /// <summary>Validated text input with an optional checkbox.</summary>
    public static (bool Ok, string Text, bool Checked) Prompt(string title, string message, string initial, Func<string, string?> validate, string? checkText = null)
    {
        var d = Create(title, message, Txt.Ok, cancellable: true);
        d._validate = validate;
        d.Input.Visibility = Visibility.Visible;
        d.Input.Text = initial;
        if (checkText is not null) { d.Check.Content = checkText; d.Check.Visibility = Visibility.Visible; }
        d.Loaded += (_, _) => { d.Input.Focus(); d.Input.SelectAll(); };
        var ok = Open(d) == DialogChoice.Primary;
        return (ok, d.Input.Text.Trim(), d.Check.IsChecked == true);
    }

    static Dialog Create(string title, string message, string okText, bool cancellable)
    {
        var d = new Dialog { Title = title };
        d.Heading.Text = title;
        d.Body.Text = message;
        d.Body.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        d.Ok.Content = okText;
        d.Cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        if (Application.Current?.MainWindow is { IsVisible: true } owner && owner != d) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.SourceInitialized += (_, _) => DarkTitleBar.Apply(d);
        // In confirmations, focus starts on "Cancel": Enter never applies anything by accident.
        d.Loaded += (_, _) =>
        {
            Motion.Pop(d.Root);
            if (d.Input.Visibility != Visibility.Visible) (cancellable ? d.Cancel : d.Ok).Focus();
        };
        return d;
    }

    static DialogChoice Open(Dialog d)
    {
        d.ShowDialog();
        return d._choice;
    }

    void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        var error = _validate?.Invoke(Input.Text.Trim());
        InputError.Text = error ?? "";
        InputError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        Ok.IsEnabled = error is null;
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (!Ok.IsEnabled) return;
        _choice = DialogChoice.Primary;
        DialogResult = true;
    }
}
