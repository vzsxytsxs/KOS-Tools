using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KosToolbox.Core.Storage;
using KosToolbox.Core.Text;

namespace KosToolbox.Views;

/// <summary>What a cell of the disk map stands for: a folder, or a file inside <see cref="Folder"/>.</summary>
public sealed record MapTarget(string Path, bool IsFile, DirNode? Node, DirNode Folder, long Size);

/// <summary>
/// The folders of one scanned folder as a treemap: a rectangle per folder with an area in proportion to its size, and inside each
/// one its own biggest folders. What a folder holds outside its subfolders (its files) is a cell of its own, drawn as one square per
/// file once that folder's files are listed. Click opens a folder, right-click offers Show in Explorer and delete, hovering says what
/// and how big. Monochrome like the rest of the toolbox; no animation.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(nameof(Root), typeof(DirNode), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Bumped when something was deleted: the listed files of every folder are read again.</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(TreemapView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapView)d).ForgetFiles()));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(TreemapView));
    public static readonly DependencyProperty RevealCommandProperty = DependencyProperty.Register(nameof(RevealCommand), typeof(ICommand), typeof(TreemapView));
    public static readonly DependencyProperty RecycleCommandProperty = DependencyProperty.Register(nameof(RecycleCommand), typeof(ICommand), typeof(TreemapView));
    public static readonly DependencyProperty DeleteCommandProperty = DependencyProperty.Register(nameof(DeleteCommand), typeof(ICommand), typeof(TreemapView));

    public DirNode? Root { get => (DirNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    /// <summary>Opens a folder (parameter: the <see cref="DirNode"/>).</summary>
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    /// <summary>Show in Explorer, the recycle and the permanent delete take a <see cref="MapTarget"/>.</summary>
    public ICommand? RevealCommand { get => (ICommand?)GetValue(RevealCommandProperty); set => SetValue(RevealCommandProperty, value); }
    public ICommand? RecycleCommand { get => (ICommand?)GetValue(RecycleCommandProperty); set => SetValue(RecycleCommandProperty, value); }
    public ICommand? DeleteCommand { get => (ICommand?)GetValue(DeleteCommandProperty); set => SetValue(DeleteCommandProperty, value); }

    const int MaxFolders = 48, MaxDepth = 3, Gap = 1, HeaderHeight = 16, MaxFiles = 150;
    // The neutral shades alternate so neighbours stay apart; file squares are a little lighter than the folders around them.
    static readonly Color[] Outer = { Color.FromRgb(0x24, 0x24, 0x24), Color.FromRgb(0x2C, 0x2C, 0x2C), Color.FromRgb(0x1C, 0x1C, 0x1C), Color.FromRgb(0x30, 0x30, 0x30) };
    static readonly Color Hot = Color.FromRgb(0x7A, 0x7A, 0x7A), FilesColor = Color.FromRgb(0x14, 0x14, 0x14), RestColor = Color.FromRgb(0x0E, 0x0E, 0x0E);

    sealed record Hit(Rect Bounds, DirNode? Node, string Text, MapTarget? Target);
    readonly List<Hit> _hits = new();
    // The files of each folder shown as "files here": null while being read.
    readonly Dictionary<DirNode, FolderFiles?> _files = new();
    Hit? _hover;

    public TreemapView()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 250);
    }

    void ForgetFiles()
    {
        _files.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, area);
        if (Root is not { Size: > 0 } root || area.Width < 8 || area.Height < 8) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Layout(dc, dpi, root, area, depth: 0);
        if (_hover is { } h) dc.DrawRectangle(null, new Pen(new SolidColorBrush(Hot), 2), Rect.Inflate(h.Bounds, -1, -1));
    }

    /// <summary>The cells of a folder's children (and one cell for its own files) inside <paramref name="area"/>.</summary>
    void Layout(DrawingContext dc, double dpi, DirNode node, Rect area, int depth)
    {
        var all = (node.Children ?? new List<DirNode>()).Where(c => c.Size > 0).ToList();
        var kids = all.Take(MaxFolders).ToList();
        var rest = node.Size - kids.Sum(c => c.Size);
        var weights = kids.Select(c => (double)c.Size).ToList();
        var more = all.Count - kids.Count;
        // What the folder holds outside the folders shown: its own files, and the small folders left out.
        if (rest > 0) weights.Add(rest);
        var cells = TreemapLayout.Squarify(weights, area.X, area.Y, area.Width, area.Height);
        foreach (var cell in cells)
        {
            var r = new Rect(cell.X + Gap / 2.0, cell.Y + Gap / 2.0, Math.Max(0, cell.Width - Gap), Math.Max(0, cell.Height - Gap));
            if (r.Width < 2 || r.Height < 2) continue;
            if (cell.Index >= kids.Count) { FilesCell(dc, dpi, node, r, rest, more); continue; }
            var child = kids[cell.Index];
            var text = $"{child.Name}\n{Txt.Size(child.Size)} · {child.Files:N0} files · {100.0 * child.Size / Math.Max(1, node.Size):0.#} % of {node.Name}";
            dc.DrawRectangle(new SolidColorBrush(Shade(cell.Index, depth)), null, r);
            _hits.Add(new Hit(r, child, text, new MapTarget(child.Path, false, child, node, child.Size)));
            var inner = r;
            var header = r.Width > 64 && r.Height > 28;
            if (header)
            {
                Label(dc, dpi, $"{child.Name}  {Txt.Size(child.Size)}", new Rect(r.X, r.Y, r.Width, HeaderHeight), dim: false);
                inner = new Rect(r.X + 2, r.Y + HeaderHeight, r.Width - 4, r.Height - HeaderHeight - 2);
            }
            // Down into the folder: its subfolders, and its files as squares (a folder with no subfolders is all files).
            if (depth < MaxDepth && header && inner.Width > 24 && inner.Height > 20) Layout(dc, dpi, child, inner, depth + 1);
        }
    }

    /// <summary>The cell for what a folder holds outside its subfolders: one square per file when they are listed and the cell is big enough, else a plain cell.</summary>
    void FilesCell(DrawingContext dc, double dpi, DirNode node, Rect r, long rest, int smallerFolders)
    {
        var summary = $"{Txt.TreemapFiles}\n{Txt.Size(rest)}" + (smallerFolders > 0 ? $" · {smallerFolders:N0} smaller folders" : "");
        dc.DrawRectangle(new SolidColorBrush(FilesColor), null, r);
        _hits.Add(new Hit(r, null, summary, null));
        if (r.Width < 28 || r.Height < 18) return;
        if (!_files.TryGetValue(node, out var files))
        {
            _files[node] = null; // being read: the folder's files only (one directory), then drawn again
            var path = node.Path;
            Task.Run(() => FolderFiles.Read(path, MaxFiles)).ContinueWith(t =>
                Dispatcher.BeginInvoke(() => { if (t.IsCompletedSuccessfully && _files.ContainsKey(node)) { _files[node] = t.Result; InvalidateVisual(); } }), TaskScheduler.Default);
        }
        if (files is null) { Label(dc, dpi, Txt.TreemapFiles + "…", r, dim: true); return; }
        if (files.Top.Count == 0) { Label(dc, dpi, Txt.TreemapFiles, r, dim: true); return; }
        var weights = files.Top.Select(f => (double)f.Size).ToList();
        if (files.RestSize > 0) weights.Add(files.RestSize); // the files that didn't make the list, as one cell
        var inner = new Rect(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
        foreach (var cell in TreemapLayout.Squarify(weights, inner.X, inner.Y, inner.Width, inner.Height))
        {
            var c = new Rect(cell.X + Gap / 2.0, cell.Y + Gap / 2.0, Math.Max(0, cell.Width - Gap), Math.Max(0, cell.Height - Gap));
            if (c.Width < 2 || c.Height < 2) continue;
            if (cell.Index < files.Top.Count)
            {
                var file = files.Top[cell.Index];
                var shade = (byte)(0x3A + cell.Index % 4 * 7);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(shade, shade, shade)), null, c);
                _hits.Add(new Hit(c, null, $"{System.IO.Path.GetFileName(file.Path)}\n{Txt.Size(file.Size)}\n{node.Path}", new MapTarget(file.Path, true, null, node, file.Size)));
                if (c.Width > 48 && c.Height > 15) Label(dc, dpi, System.IO.Path.GetFileName(file.Path), c, dim: false);
            }
            else
            {
                dc.DrawRectangle(new SolidColorBrush(RestColor), null, c);
                _hits.Add(new Hit(c, null, $"{files.RestCount:N0} smaller files\n{Txt.Size(files.RestSize)}", null));
                if (c.Width > 60 && c.Height > 15) Label(dc, dpi, $"{files.RestCount:N0} smaller files", c, dim: true);
            }
        }
    }

    static Color Shade(int index, int depth)
    {
        var baseColor = Outer[(index + depth * 2) % Outer.Length];
        if (depth == 0) return baseColor;
        var lift = (byte)(0x0C + depth * 0x0C + index % 4 * 5); // each level a little lighter, neighbours a little apart
        return Color.FromRgb((byte)(baseColor.R + lift), (byte)(baseColor.G + lift), (byte)(baseColor.B + lift));
    }

    void Label(DrawingContext dc, double dpi, string text, Rect r, bool dim)
    {
        if (r.Width < 30) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            dim ? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)) : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), dpi)
        { MaxTextWidth = Math.Max(1, r.Width - 6), MaxTextHeight = Math.Max(1, Math.Min(r.Height, 16)), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(r.X + 4, r.Y + 1));
    }

    Hit? HitAt(Point p)
    {
        for (var i = _hits.Count - 1; i >= 0; i--) if (_hits[i].Bounds.Contains(p)) return _hits[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        ToolTip = hit?.Text;
        Cursor = hit?.Node?.Children is { Count: > 0 } ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        ToolTip = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitAt(e.GetPosition(this))?.Node is { Children: { Count: > 0 } } node && OpenCommand?.CanExecute(node) == true) OpenCommand.Execute(node);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitAt(e.GetPosition(this))?.Target is not { } target) return;
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Add(string header, ICommand? command)
        {
            if (command is null) return;
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => { if (command.CanExecute(target)) command.Execute(target); };
            menu.Items.Add(item);
        }
        Add(Txt.MenuShowInExplorer, RevealCommand);
        menu.Items.Add(new Separator());
        Add(Txt.MenuRecycle, RecycleCommand);
        Add(Txt.MenuDelete, DeleteCommand);
        menu.IsOpen = true;
    }
}
