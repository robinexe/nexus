using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus.Desktop.Views.Shell;
using Nexus.DiskTools.Analysis;

namespace Nexus.Desktop.Views.Modules.Disk;

/// <summary>
/// Treemap do disco: cada bloco tem área proporcional ao tamanho (layout "squarified" de <see cref="Treemap"/>, na biblioteca do
/// módulo). Pastas em ciano (clicáveis: entram na pasta), arquivos coloridos pelo tipo, "outros arquivos" em cinza. Um único
/// elemento desenhado em OnRender (centenas de blocos sem um elemento por bloco). Somente leitura.
/// Quando os blocos mudam (nova análise, entrar numa pasta), eles crescem do centro até o tamanho real em 0,45 s; com as
/// animações do Windows desligadas, aparecem prontos.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private const double Gap = 2;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(nameof(Tiles), typeof(IReadOnlyList<DiskTile>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapView)d).OnTilesChanged()));

    /// <summary>Quanto os blocos já cresceram (0..1), animado a cada troca de blocos.</summary>
    private static readonly DependencyProperty GrowProperty = DependencyProperty.Register("Grow", typeof(double), typeof(TreemapView),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(TreemapView));

    private static readonly Brush FolderFill = Frozen(0x16, 0x3A, 0x48);
    private static readonly Brush FolderFillHover = Frozen(0x1E, 0x55, 0x66);
    private static readonly Pen FolderPen = FrozenPen(0x4F, 0xD1, 0xE8, 0.55);
    private static readonly Pen HoverPen = FrozenPen(0xE8, 0xEC, 0xF5, 1.5);
    private static readonly Brush OtherFill = Frozen(0x2A, 0x33, 0x4A);
    private static readonly Brush LabelBrush = Frozen(0xE8, 0xEC, 0xF5);
    private static readonly Brush DimLabelBrush = Frozen(0x8A, 0x96, 0xB3);
    private static readonly Brush DarkLabelBrush = Frozen(0x0E, 0x13, 0x20);

    private IReadOnlyList<TreemapRect> _rects = [];
    private int _hover = -1;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public TreemapView()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
        ToolTipService.SetInitialShowDelay(this, 250);
        MouseLeave += (_, _) => SetHover(-1);
    }

    private void OnTilesChanged()
    {
        _hover = -1;
        BeginAnimation(GrowProperty, null);
        if (!ShellMotion.Enabled)
        {
            SetValue(GrowProperty, 1d);
            return;
        }
        BeginAnimation(GrowProperty, new DoubleAnimation(0.15, 1, new Duration(TimeSpan.FromMilliseconds(450)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    public IReadOnlyList<DiskTile>? Tiles { get => (IReadOnlyList<DiskTile>?)GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    /// <summary>Executado com o <see cref="DiskTile"/> clicado.</summary>
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(Frozen(r, g, b), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>Cores por tipo (as mesmas da legenda e dos cartões: <see cref="DiskPalette"/>), texto escuro por cima.</summary>
    private static readonly Dictionary<FileCategory, Brush> CategoryBrushes = Enum.GetValues<FileCategory>()
        .ToDictionary(c => c, c => (Brush)FrozenColor((Color)ColorConverter.ConvertFromString(DiskPalette.Hex(c))));

    private static Brush FileFill(FileCategory? c) => CategoryBrushes[c ?? FileCategory.Other];

    private static SolidColorBrush FrozenColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 420 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var tiles = Tiles;
        var w = ActualWidth;
        var h = ActualHeight;
        if (tiles is null || tiles.Count == 0 || !(w >= 10) || !(h >= 10))
        {
            _rects = [];
            return;
        }
        _rects = Treemap.Squarify(tiles.Select(t => (double)t.Size).ToList(), new TreemapRect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.Body") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var numbers = new Typeface(TryFindResource("Font.Numbers") as FontFamily ?? new FontFamily("Bahnschrift SemiCondensed"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var grow = Math.Clamp((double)GetValue(GrowProperty), 0, 1);

        for (var i = 0; i < tiles.Count; i++)
        {
            var r = _rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            var rect = new Rect(r.X + Gap / 2, r.Y + Gap / 2, Math.Max(0, r.Width - Gap), Math.Max(0, r.Height - Gap));
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            if (grow < 1)
            {
                var cx = rect.X + rect.Width / 2;
                var cy = rect.Y + rect.Height / 2;
                rect = new Rect(cx - rect.Width * grow / 2, cy - rect.Height * grow / 2, rect.Width * grow, rect.Height * grow);
            }
            var tile = tiles[i];
            var hover = i == _hover;
            var (fill, pen, label) = tile.Kind switch
            {
                DiskTileKind.Folder => (hover ? FolderFillHover : FolderFill, hover ? HoverPen : FolderPen, LabelBrush),
                DiskTileKind.File => (FileFill(tile.Category), hover ? HoverPen : null, DarkLabelBrush),
                _ => (OtherFill, hover ? HoverPen : null, DimLabelBrush),
            };
            drawingContext.DrawRoundedRectangle(fill, pen, rect, 3, 3);

            if (grow < 1 || rect.Width < 54 || rect.Height < 20) continue;
            drawingContext.PushClip(new RectangleGeometry(rect));
            var name = new FormattedText(tile.Label, Pt, FlowDirection.LeftToRight, typeface, 12, label, dpi)
            {
                MaxTextWidth = Math.Max(1, rect.Width - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawingContext.DrawText(name, new Point(rect.X + 6, rect.Y + 4));
            if (rect.Height >= 38)
            {
                var size = new FormattedText(Format.Bytes(tile.Size), Pt, FlowDirection.LeftToRight, numbers, 12, label, dpi)
                {
                    MaxTextWidth = Math.Max(1, rect.Width - 10),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                drawingContext.DrawText(size, new Point(rect.X + 6, rect.Y + 20));
            }
            drawingContext.Pop();
        }
    }

    private int HitTest(Point p)
    {
        for (var i = 0; i < _rects.Count; i++)
        {
            var r = _rects[i];
            if (r.Width >= 1 && r.Height >= 1 && p.X >= r.X && p.X < r.Right && p.Y >= r.Y && p.Y < r.Bottom) return i;
        }
        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        var tiles = Tiles;
        if (index >= 0 && tiles is not null && index < tiles.Count)
        {
            var t = tiles[index];
            _tip.Content = t.Kind switch
            {
                DiskTileKind.Folder => $"{t.Label}\n{Format.Bytes(t.Size)} · clique para abrir a pasta",
                DiskTileKind.File => $"{t.Label}\n{Format.Bytes(t.Size)} · {FileCategories.Label(t.Category ?? FileCategory.Other)}\n{t.File?.Folder}",
                _ => $"{t.Label}\n{Format.Bytes(t.Size)}",
            };
            Cursor = t.Kind == DiskTileKind.Folder ? Cursors.Hand : Cursors.Arrow;
            if (_tip.IsOpen) _tip.IsOpen = false; // reabre junto do novo bloco
            ToolTip = _tip;
        }
        else
        {
            _tip.IsOpen = false;
            ToolTip = null;
            Cursor = Cursors.Arrow;
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        SetHover(HitTest(e.GetPosition(this)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonUp(e);
        var i = HitTest(e.GetPosition(this));
        var tiles = Tiles;
        if (i < 0 || tiles is null || i >= tiles.Count) return;
        var cmd = OpenCommand;
        if (cmd?.CanExecute(tiles[i]) == true) cmd.Execute(tiles[i]);
        e.Handled = true;
    }
}
