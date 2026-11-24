using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Xehn.App;

// Segoe Fluent Icons is a fixed-weight font: FontWeight=Light alone does not
// thin its strokes. Inset the existing glyph contour by 0.15 DIP on each edge,
// retaining its exact symbol, font metrics, layout and selection transform.
public sealed class NavigationGlyph : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(NavigationGlyph), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(NavigationGlyph), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedForegroundProperty = DependencyProperty.Register(nameof(SelectedForeground), typeof(Brush), typeof(NavigationGlyph), new FrameworkPropertyMetadata(Brushes.DarkOrange, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public Brush SelectedForeground { get => (Brush)GetValue(SelectedForegroundProperty); set => SetValue(SelectedForegroundProperty, value); }
    public Brush DrawingBrush => IsSelected ? SelectedForeground : Foreground;
    private Geometry? source, outline, filled;
    private string? signature;
    public NavigationGlyph() { Focusable = false; IsTabStop = false; IsHitTestVisible = false; SetResourceReference(ForegroundProperty, "InkBrush"); SetResourceReference(SelectedForegroundProperty, "Accent"); }
    private FormattedText Format(string? text = null) => new(text ?? Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    public Geometry DrawingGeometry => IsSelected ? FilledGeometry : OutlineGeometry;
    public Geometry OutlineGeometry
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var key = $"{Text}|{FontFamily.Source}|{FontSize}|{FontWeight}|{FontStyle}|{FontStretch}|{dpi}";
            if (outline != null && signature == key) return outline;
            source = Format().BuildGeometry(new Point()); filled = null;
            var edge = source.GetWidenedPathGeometry(new Pen(Brushes.Black, .3), .01, ToleranceType.Absolute);
            outline = Geometry.Combine(source, edge, GeometryCombineMode.Exclude, null, .01, ToleranceType.Absolute);
            outline.Freeze(); signature = key;
            return outline;
        }
    }
    public Geometry FilledGeometry
    {
        get
        {
            _ = OutlineGeometry;
            if (filled != null) return filled;
            // Native solid counterparts retain the same font and layout metrics.
            var solid = Text switch { "\uE80F" => "\uEA8A", "\uE713" => "\uF8B0", "\uE946" => "\uF167", _ => Text };
            if (Text == "\uE9D9")
            {
                // Diagnostic has no font solid counterpart. Fill its frame and
                // knock out the original trace, keeping that symbol recognizable.
                var bounds = source!.Bounds;
                var inside = bounds; inside.Inflate(-1.5, -1.5);
                var trace = Geometry.Combine(source, new RectangleGeometry(inside), GeometryCombineMode.Intersect, null);
                filled = Geometry.Combine(new RectangleGeometry(bounds, .5, .5), trace, GeometryCombineMode.Exclude, null);
            }
            else filled = Format(solid).BuildGeometry(new Point());
            filled.Freeze(); return filled;
        }
    }
    protected override Size MeasureOverride(Size constraint) { var text = Format(); return new Size(text.WidthIncludingTrailingWhitespace, text.Height); }
    protected override void OnRender(DrawingContext drawingContext) => drawingContext.DrawGeometry(DrawingBrush, null, DrawingGeometry);
}
