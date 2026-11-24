using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xehn.App;

internal static class NavigationChecks
{
    public static void Run(Action<bool, string> check)
    {
        var host = new Grid { Width = 92, VerticalAlignment = VerticalAlignment.Top };
        var panel = new StackPanel(); host.Children.Add(panel);
        string[] pages = ["Home", "Settings", "About", "Logs"];
        var buttons = pages.Select((page, i) => new RadioButton
        {
            Tag = page, GroupName = "FixtureNavigation", Style = (Style)Application.Current.FindResource("Nav"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new NavigationGlyph { Text = new[] { "\uE80F", "\uE713", "\uE946", "\uE9D9" }[i], FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 0, 10, 0) }, new TextBlock { Text = page[..1], FontSize = 16 } } }
        }).ToArray();
        foreach (var button in buttons) panel.Children.Add(button);
        foreach (var button in buttons) ((NavigationGlyph)((StackPanel)button.Content).Children[0]).SetBinding(NavigationGlyph.IsSelectedProperty, new System.Windows.Data.Binding("IsChecked") { Source = button });
        foreach (var button in buttons.Where(b => (string)b.Tag is "About" or "Logs")) ((NavigationGlyph)((StackPanel)button.Content).Children[0]).Margin = new Thickness(0, 2, 10, -2);
        buttons[0].IsChecked = true;
        var indicator = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.DarkOrange, IsHitTestVisible = false };
        host.Children.Add(indicator);
        using var motion = new NavigationMotion(host, indicator, buttons);
        var fixture = new Window { Content = host, Width = 120, Height = 240, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        double Target(RadioButton button) => button.TranslatePoint(new Point(0, (button.ActualHeight - indicator.Height) / 2), host).Y;
        TransformGroup Transforms(RadioButton button) => (TransformGroup)((StackPanel)button.Content).Children.OfType<FrameworkElement>().First().RenderTransform;
        bool Neutral(RadioButton button) { var t = Transforms(button); return ((ScaleTransform)t.Children[0]).ScaleX == 1 && ((ScaleTransform)t.Children[0]).ScaleY == 1 && ((RotateTransform)t.Children[1]).Angle == 0 && ((TranslateTransform)t.Children[2]).X == 0 && ((TranslateTransform)t.Children[2]).Y == 0; }
        try
        {
            fixture.Show(); Pump();
            check(motion.TransitionCount == 0 && !motion.IsAnimating && motion.IndicatorOffset == Target(buttons[0]), "navigation initial selection places one indicator without an entrance animation");
            foreach (var button in buttons.Skip(1).Append(buttons[0]))
            {
                var from = motion.IndicatorOffset;
                var count = motion.TransitionCount;
                button.IsChecked = true; Pump();
                var selectedGlyph = (NavigationGlyph)((StackPanel)button.Content).Children[0];
                check(selectedGlyph.IsSelected && ReferenceEquals(selectedGlyph.DrawingBrush, Application.Current.FindResource("Accent")) && ReferenceEquals(selectedGlyph.DrawingGeometry, selectedGlyph.FilledGeometry) && buttons.Where(b => b != button).All(b => !((NavigationGlyph)((StackPanel)b.Content).Children[0]).IsSelected), button.Tag + " switches only the selected glyph to accent solid fill and restores previous icons to outlines");
                if (SystemParameters.ClientAreaAnimation)
                {
                    check(motion.IsAnimating && motion.TransitionCount == count + 1, button.Tag + " selection starts a finite indicator and icon transition");
                    Wait(() => !Neutral(button));
                    check(!Neutral(button), button.Tag + " glyph actually changes its rendered transform during selection");
                    if ((string)button.Tag is "Home" or "Logs")
                    {
                        var slide = (TranslateTransform)Transforms(button).Children[2];
                        check(slide.X == 0 && slide.Y < 0, button.Tag + " uses the same vertical bounce without horizontal motion");
                    }
                    if ((string)button.Tag == "Settings")
                    {
                        Wait(() => motion.IndicatorOffset > from && motion.IndicatorOffset < Target(button));
                        check(motion.IndicatorOffset > from && motion.IndicatorOffset < Target(button), "selection pill traverses intermediate positions instead of teleporting");
                    }
                    Wait(() => !motion.IsAnimating);
                }
                else check(!motion.IsAnimating && motion.TransitionCount == count, "system reduced-motion preference skips " + button.Tag + " transition");
                check(motion.IndicatorOffset == Target(button) && buttons.All(Neutral) && !motion.IsAnimating, button.Tag + " transition lands exactly and releases every icon/indicator clock");
                count = motion.TransitionCount;
                button.IsChecked = true; button.DataContext = new object(); button.IsEnabled = false; button.IsEnabled = true; Pump();
                check(motion.TransitionCount == count && !motion.IsAnimating, "same selection, model updates and enablement cannot replay " + button.Tag + " animation");
            }
            buttons[3].IsChecked = true; buttons[1].IsChecked = true; buttons[2].IsChecked = true; Pump(); Wait(() => !motion.IsAnimating);
            check(motion.IndicatorOffset == Target(buttons[2]) && buttons.All(Neutral), "rapid navigation cancels obsolete clocks and settles on only the latest page");
            buttons[1].IsChecked = true; Pump(); fixture.Hide(); Pump();
            check(!motion.IsAnimating && motion.IndicatorOffset == Target(buttons[1]) && buttons.All(Neutral), "hiding navigation stops motion and preserves the selected page");
            var transitions = motion.TransitionCount; fixture.Show(); Pump();
            check(!motion.IsAnimating && motion.TransitionCount == transitions, "restoring navigation does not replay selection animations");
            var surface = (Border)buttons[1].Template.FindName("SelectionSurface", buttons[1]);
            App.ApplyTheme("dark"); Pump();
            check(ReferenceEquals(((NavigationGlyph)((StackPanel)buttons[1].Content).Children[0]).DrawingBrush, Application.Current.FindResource("Accent")), "selected solid icon retains its accent fill in dark theme");
            check(ReferenceEquals(surface.Background, Application.Current.FindResource("NavigationSelectedBrush")) && !motion.IsAnimating && surface.CornerRadius == new CornerRadius(4), "native-style selection surface follows dark theme without triggering motion");
            App.ApplyTheme("light"); Pump();
            check(ReferenceEquals(surface.Background, Application.Current.FindResource("NavigationSelectedBrush")), "selection surface returns to the light theme without a stale dark fill");
            buttons[0].IsChecked = true; Pump(); motion.Dispose();
            transitions = motion.TransitionCount; buttons[3].IsChecked = true; Pump();
            check(!motion.IsAnimating && motion.TransitionCount == transitions && buttons.All(Neutral), "disposing navigation releases active clocks and selection handlers");
            using var reduced = new NavigationMotion(host, indicator, buttons, () => false);
            buttons[1].IsChecked = true; Pump();
            check(!reduced.IsAnimating && reduced.TransitionCount == 0 && reduced.IndicatorOffset == Target(buttons[1]) && buttons.All(Neutral), "reduced-motion path keeps selection feedback and glyphs exact without animation");
            fixture.Close(); Pump();
            check(!reduced.IsAnimating, "unloaded navigation retains no animation clocks");
        }
        finally { fixture.Close(); App.ApplyTheme("light"); Pump(); }
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void Wait(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Navigation animation fixture did not settle"); Thread.Sleep(5); Pump(); }
    }
}
