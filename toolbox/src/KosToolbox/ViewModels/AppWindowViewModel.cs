using System.Windows.Input;

namespace KosToolbox.ViewModels;

/// <summary>
/// The window of a KOS app that is a single page (KOS Firewall, KOS Equalizer): that page under the toolbox's header, with its
/// toasts and theme (<see cref="Views.AppWindow"/>).
/// </summary>
public sealed class AppWindowViewModel : ObservableObject
{
    public AppWindowViewModel(string title, PageViewModel page)
    {
        Title = title;
        Current = page;
        RefreshCommand = new AsyncCommand(page.RefreshAsync);
    }

    public string Title { get; }
    public PageViewModel Current { get; }
    public ICommand RefreshCommand { get; }
}

/// <summary>
/// A sidebar entry (the KOS Apps group) that opens another KOS app instead of a page: a button, never selected, so the page
/// that's open stays open. An app that already runs comes to the front instead of starting again.
/// </summary>
public sealed class AppLinkViewModel(string key, string title, string glyph, string subtitle, string folder, string exe, string? iconPath = null)
    : PageViewModel(key, title, glyph, subtitle)
{
    RelayCommand? _open;

    public override string? IconPath => iconPath;
    public ICommand OpenCommand => _open ??= new RelayCommand(Open);

    public void Open()
    {
        try { Core.Execution.KosApps.Start(folder, exe); }
        catch (Exception ex)
        {
            Core.Persistence.Log.Error($"Opening {exe} failed", ex);
            Toasts.Show(ex.Message, ToastKind.Error);
        }
    }
}
