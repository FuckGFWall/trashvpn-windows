using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Xehn.App;

internal static class ContentSizingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var combo = new ContentComboBox { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var label in new[] { "短", "MASQUE-in-MASQUE · 最长选项", "中等文本" }) combo.Items.Add(new ComboBoxItem { Content = label });
        combo.SelectedIndex = 0;
        var intensity = new ScanIntensitySlider();
        var state = new State(); intensity.SetBinding(ScanIntensitySlider.IntensityProperty, new Binding(nameof(State.Intensity)) { Source = state });
        var panel = new StackPanel(); panel.Children.Add(combo); panel.Children.Add(intensity);
        var fixture = new Window { Content = panel, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Width = 400, Height = 200, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            fixture.Show(); Pump();
            var shortWidth = combo.ActualWidth;
            check(combo.SelectedLabel == "短" && Math.Abs(shortWidth - combo.SelectionWidth) <= 1 && shortWidth < 80, "short selected labels produce genuinely compact controls");
            var toggle = (ToggleButton)combo.Template.FindName("SelectionToggle", combo);
            var label = (TextBlock)toggle.Template.FindName("SelectionLabel", toggle);
            var arrow = (FrameworkElement)toggle.Template.FindName("SelectionChevron", toggle);
            var leftInset = label.TranslatePoint(new Point(), combo).X - 1;
            var labelGap = arrow.TranslatePoint(new Point(), combo).X - label.TranslatePoint(new Point(label.ActualWidth, 0), combo).X;
            check(Math.Abs(leftInset - 10) <= 1 && Math.Abs(labelGap - leftInset) <= 1, "selected label keeps the existing 10-DIP left inset and an equal gap before the chevron");
            var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
            var surface = (FrameworkElement)popup.Child;
            double MeasurePopup()
            {
                surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
                return surface.ActualWidth;
            }
            var popupWidth = MeasurePopup();
            check(double.IsNaN(surface.Width) && surface.MinWidth == 0 && BindingOperations.GetBindingBase(surface, FrameworkElement.MinWidthProperty) == null && popupWidth > shortWidth + 70, $"popup independently measures its longest option, not the closed control (popup {popupWidth:0.0}, control {shortWidth:0.0})");
            combo.SelectedIndex = 1; Pump();
            check(combo.ActualWidth > shortWidth + 70 && combo.SelectedLabel.EndsWith("最长选项"), "selecting a longer label grows only the closed selector to fit that current text");
            var longWidth = combo.ActualWidth;
            var secondPopupWidth = MeasurePopup();
            combo.SelectedIndex = 0; Pump();
            check(Math.Abs(combo.ActualWidth - shortWidth) <= 1 && Math.Abs(secondPopupWidth - popupWidth) <= 1, "returning to a short option shrinks the selector while the dropdown retains longest-option sizing");
            combo.FontSize = 16; Pump();
            check(combo.ActualWidth > shortWidth && combo.ActualWidth < longWidth, "content sizing follows actual font metrics rather than character counts");
            combo.FontSize = 13;
            combo.Items.Clear(); combo.DisplayMemberPath = "Label";
            combo.ItemsSource = new[] { new NodeExportFormat("short", "Clash"), new NodeExportFormat("long", "v2rayN / Xray JSON") };
            combo.SelectedIndex = 1; Pump();
            check(combo.SelectedLabel == "v2rayN / Xray JSON" && combo.ActualWidth == combo.SelectionWidth, "data-bound format objects measure their DisplayMemberPath label instead of a record representation");
            var formatWidth = combo.ActualWidth;
            App.ApplyTheme("dark"); Pump();
            check(combo.ActualWidth == formatWidth && ReferenceEquals(label.Foreground, Application.Current.FindResource("InkBrush")), "theme changes retain measured widths and update the selector text color");
            App.ApplyTheme("light"); Pump();

            var track = (Slider)intensity.FindName("IntensityTrack");
            track.ApplyTemplate(); track.UpdateLayout();
            var thumb = ((Track)track.Template.FindName("PART_Track", track)).Thumb;
            void StartDrag() => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            void EndDrag(bool canceled = false) => thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
            check(track.Minimum == 0 && track.Maximum == 2 && track.IsSnapToTickEnabled && track.TickFrequency == 1 && track.IsMoveToPointEnabled && track.Value == 1 && state.Writes == 0, "intensity slider starts at the saved middle stop without writing settings");
            check(ReferenceEquals(thumb.Background, Application.Current.FindResource("Accent")) && ReferenceEquals(((Border)track.Template.FindName("PART_SelectedRange", track)).Background, Application.Current.FindResource("Accent")), "native Fluent thumb and progress fill use the app orange accent, not the system blue");
            StartDrag(); track.Value = 0; thumb.RaiseEvent(new DragDeltaEventArgs(track.ActualWidth, 0) { RoutedEvent = Thumb.DragDeltaEvent }); Pump();
            check(state.Intensity == "medium" && state.Writes == 0 && track.Value == 2, "dragging previews strength without repeatedly saving or restarting node preparation");
            EndDrag(); Pump();
            check(state.Intensity == "high" && state.Writes == 1 && intensity.Intensity == "high", "releasing the thumb commits the final high strength exactly once");
            StartDrag(); track.Value = 0; EndDrag(true); Pump();
            check(state.Intensity == "high" && state.Writes == 1 && track.Value == 2, "canceling a drag restores the saved strength without a spurious settings write");
            track.Value = 0; Pump();
            check(state.Intensity == "low" && state.Writes == 2, "non-drag keyboard or track changes commit the low stop immediately");
            track.Value = 1; Pump();
            check(state.Intensity == "medium" && state.Writes == 3, "middle stop maps back to the existing medium settings value");
            intensity.IsEnabled = false; track.Value = 2; Pump();
            check(state.Writes == 3 && track.Value == 1, "disabled strength controls cannot change settings");
            intensity.IsEnabled = true; StartDrag(); track.Value = 2; intensity.Visibility = Visibility.Collapsed; Pump();
            check(state.Writes == 3 && track.Value == 1, "leaving the page cancels an unfinished preview rather than saving a stale drag");
            intensity.Visibility = Visibility.Visible; state.Intensity = "high"; Pump();
            check(track.Value == 2 && intensity.Intensity == "high", "external saved-strength updates synchronize the slider without feedback loops");
            App.ApplyTheme("dark"); Pump(); track.ApplyTemplate();
            check(ReferenceEquals(((Track)track.Template.FindName("PART_Track", track)).Thumb.Background, Application.Current.FindResource("Accent")), "native strength slider retains the orange accent after dark-theme template replacement");
        }
        finally { fixture.Close(); App.ApplyTheme("light"); Pump(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private sealed class State : INotifyPropertyChanged
    {
        private string value = "medium";
        public int Writes { get; private set; }
        public string Intensity { get => value; set { if (this.value == value) return; this.value = value; Writes++; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Intensity))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
