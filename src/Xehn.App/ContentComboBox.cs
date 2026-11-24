using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Xehn.App;

// The closed control measures only the selected label. Its popup deliberately
// has no width binding to this control: WPF measures the full item list itself.
public sealed class ContentComboBox : ComboBox
{
    private static readonly DependencyPropertyKey SelectedLabelPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectedLabel), typeof(string), typeof(ContentComboBox), new PropertyMetadata(""));
    private static readonly DependencyPropertyKey SelectionWidthPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectionWidth), typeof(double), typeof(ContentComboBox), new PropertyMetadata(40d));
    private static readonly DependencyPropertyKey SelectionDirectionPropertyKey = DependencyProperty.RegisterReadOnly(nameof(SelectionDirection), typeof(FlowDirection), typeof(ContentComboBox), new PropertyMetadata(FlowDirection.LeftToRight));
    public static readonly DependencyProperty SelectedLabelProperty = SelectedLabelPropertyKey.DependencyProperty;
    public static readonly DependencyProperty SelectionWidthProperty = SelectionWidthPropertyKey.DependencyProperty;
    public static readonly DependencyProperty SelectionDirectionProperty = SelectionDirectionPropertyKey.DependencyProperty;
    private static readonly DependencyProperty SelectedContentProperty = DependencyProperty.Register(
        "SelectedContent", typeof(object), typeof(ContentComboBox), new PropertyMetadata(null, (d, _) => ((ContentComboBox)d).UpdateWidth()));
    public string SelectedLabel => (string)GetValue(SelectedLabelProperty);
    public double SelectionWidth => (double)GetValue(SelectionWidthProperty);
    public FlowDirection SelectionDirection => (FlowDirection)GetValue(SelectionDirectionProperty);
    public ContentComboBox() { Loaded += (_, _) => UpdateWidth(); }
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (SelectedItem is ComboBoxItem item)
            SetBinding(SelectedContentProperty, new Binding(nameof(ContentControl.Content)) { Source = item });
        else ClearValue(SelectedContentProperty);
        UpdateWidth();
    }
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e) { base.OnItemsChanged(e); UpdateWidth(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); UpdateWidth(); }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty || e.Property == FontWeightProperty || e.Property == FontStyleProperty || e.Property == FontStretchProperty || e.Property == DisplayMemberPathProperty) UpdateWidth();
    }
    private void UpdateWidth()
    {
        object? label = SelectedItem;
        if (label is ComboBoxItem item) label = item.Content;
        else if (label != null && !string.IsNullOrEmpty(DisplayMemberPath))
            foreach (var part in DisplayMemberPath.Split('.')) label = label == null ? null : TypeDescriptor.GetProperties(label)[part]?.GetValue(label);
        var text = Convert.ToString(label, CultureInfo.CurrentUICulture) ?? "";
        var leadingLetter = text.FirstOrDefault(char.IsLetter);
        var direction = leadingLetter is >= '\u0590' and <= '\u08ff' ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SetValue(SelectedLabelPropertyKey, text);
        SetValue(SelectionDirectionPropertyKey, direction);
        if (FontFamily == null || FontSize <= 0) return;
        var measured = new FormattedText(text, CultureInfo.CurrentUICulture, direction,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // Border 1 + left inset 10 + label + gap 10 + chevron 8 + right inset 10 + border 1.
        SetValue(SelectionWidthPropertyKey, Math.Ceiling(measured.WidthIncludingTrailingWhitespace) + 40);
    }
}
