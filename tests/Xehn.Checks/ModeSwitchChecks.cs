using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xehn.App;

internal static class ModeSwitchChecks
{
    public static byte[] Pixels(FrameworkElement element)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)); var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var data = new byte[width * height * 4]; bitmap.CopyPixels(data, width * 4, 0); return data;
    }
    public static void Run(Action<bool, string> check)
    {
        var state = new State(); var control = new ModeSwitch { Width = 208, FirstText = "代理", SecondText = "TUN" };
        control.SetBinding(ModeSwitch.IsSecondSelectedProperty, new Binding(nameof(State.Second)) { Source = state, Mode = BindingMode.OneWay });
        var fixture = new Window { Width = 240, Height = 90, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Content = control };
        try
        {
            fixture.Show(); Pump();
            var thumb = (Border)control.FindName("SelectionThumb");
            check(control.ActualHeight == 46 && thumb.ActualWidth == 100 && control.SelectionOffset == 0, "taller, narrower mode switch has one shared rounded thumb");
            var normal = Pixels(control); var transitions = control.TransitionCount;
            control.IsEnabled = false; Pump();
            check(Pixels(control).SequenceEqual(normal) && thumb.Opacity == 1 && control.TransitionCount == transitions, "disabling mode selection cannot fade / flash its selected thumb");
            control.IsEnabled = true;
            for (var i = 0; i < 6; i++) state.Notify(); Pump();
            check(Pixels(control).SequenceEqual(normal) && control.TransitionCount == transitions, "unchanged binding refreshes never restart a selection animation");
            var requests = 0; control.SelectionRequested += (_, e) => { if (e.Second) requests++; };
            ((RadioButton)control.FindName("SecondChoice")).IsChecked = true; Pump();
            check(requests == 1 && !control.IsSecondSelected && control.SelectionOffset == 0 && !control.IsAnimating, "pending / rejected selection stays at the confirmed mode without a there-and-back flash");
            state.Second = true; state.Notify(); Pump();
            if (SystemParameters.ClientAreaAnimation)
            {
                check(control.IsAnimating && control.TransitionCount == transitions + 1, "confirmed mode starts a horizontal eased slide");
                Wait(() => control.SelectionOffset > 1 && control.SelectionOffset < 99);
                var middle = control.SelectionOffset;
                check(middle > 1 && middle < 99, "mode thumb traverses intermediate positions instead of jumping between cards");
                state.Second = false; state.Notify(); Pump();
                check(control.IsAnimating && Math.Abs(control.SelectionOffset - middle) < 25, "interrupted slide reverses from its current visible position");
                Wait(() => !control.IsAnimating);
            }
            else { check(!control.IsAnimating && control.SelectionOffset == 100, "reduced-motion system preference places thumb directly without animation"); state.Second = false; state.Notify(); Pump(); }
            check(control.SelectionOffset == 0 && !control.IsAnimating, "finished mode animation removes its clock and lands exactly on the target");
            state.Second = true; state.Notify(); Pump(); fixture.Hide(); Pump();
            check(!control.IsAnimating && control.SelectionOffset == 100, "hiding the UI releases selection animation clocks without changing confirmed mode");
            App.ApplyTheme("dark");
            check(ReferenceEquals(thumb.Background, Application.Current.FindResource("CardBrush")), "the same moving thumb follows dark theme without replacing its visual");
        }
        finally { fixture.Close(); App.ApplyTheme("light"); Pump(); }
        check(!control.IsAnimating, "closed mode switch retains no animation clock");
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    private static void Wait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Mode animation fixture did not settle"); Thread.Sleep(5); Pump(); }
    }
    private sealed class State : INotifyPropertyChanged
    {
        public bool Second { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}
