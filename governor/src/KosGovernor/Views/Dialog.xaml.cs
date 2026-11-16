using System.Windows;
using System.Windows.Controls;

namespace KosGovernor.Views;

/// <summary>Confirmation, warning and validated text input.</summary>
public partial class Dialog : Window
{
    Func<string, string?>? _validate;

    Dialog() => InitializeComponent();

    public static bool Confirm(string title, string message, string confirmText) => Create(title, message, confirmText, cancellable: true).ShowDialog() == true;

    public static void Show(string title, string message) => Create(title, message, "OK", cancellable: false).ShowDialog();

    public static (bool Ok, string Text) Prompt(string title, string message, string initial, Func<string, string?> validate)
    {
        var d = Create(title, message, "OK", cancellable: true);
        d._validate = validate;
        d.Input.Visibility = Visibility.Visible;
        d.Input.Text = initial;
        d.Loaded += (_, _) => { d.Input.Focus(); d.Input.SelectAll(); };
        var ok = d.ShowDialog() == true;
        return (ok, d.Input.Text.Trim());
    }

    static Dialog Create(string title, string message, string okText, bool cancellable)
    {
        var d = new Dialog { Title = title };
        d.Heading.Text = title;
        d.Body.Text = message;
        d.Body.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        d.Ok.Content = okText;
        d.Cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        DialogSetup.Prepare(d, d.Root);
        // On confirmations focus starts on Cancel: Enter doesn't confirm by mistake.
        d.Loaded += (_, _) => { if (d.Input.Visibility != Visibility.Visible) (cancellable ? d.Cancel : d.Ok).Focus(); };
        return d;
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
        if (Ok.IsEnabled) DialogResult = true;
    }
}
