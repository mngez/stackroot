using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views;

public partial class SessionActivityTray : UserControl
{
    public SessionActivityTray()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TrayPopup.Closed += (_, _) =>
        {
            if (DataContext is SessionActivityTrayViewModel vm && vm.IsOpen)
            {
                vm.IsOpen = false;
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SessionActivityTrayViewModel oldVm)
        {
            oldVm.PulseRequested -= OnPulseRequested;
        }

        if (e.NewValue is SessionActivityTrayViewModel newVm)
        {
            newVm.PulseRequested += OnPulseRequested;
        }
    }

    private void OnPulseRequested(object? sender, EventArgs e)
    {
        PlayAttentionPulse();
    }

    private void PlayAttentionPulse()
    {
        const double durationMs = 140;

        var badgeScaleX = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMs * 4)
        };
        badgeScaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        badgeScaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        badgeScaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs * 2))));
        badgeScaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs * 3))));
        badgeScaleX.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs * 4))));

        var badgeScaleY = badgeScaleX.Clone();
        BadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, badgeScaleX);
        BadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, badgeScaleY);

        var ringOpacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(700)
        };
        ringOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        ringOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        ringOpacity.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));

        PulseRing.BeginAnimation(UIElement.OpacityProperty, ringOpacity);

        if (BellIcon.Foreground is SolidColorBrush brush)
        {
            var animated = brush.IsFrozen ? brush.Clone() : brush;
            if (animated.IsFrozen)
            {
                animated = animated.Clone();
            }

            BellIcon.Foreground = animated;
            var original = animated.Color;
            var bellFlash = new ColorAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(500)
            };
            bellFlash.KeyFrames.Add(new EasingColorKeyFrame(original, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bellFlash.KeyFrames.Add(new EasingColorKeyFrame(
                System.Windows.Media.Color.FromRgb(0xE9, 0xBD, 0x5B),
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
            bellFlash.KeyFrames.Add(new EasingColorKeyFrame(original, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
            animated.BeginAnimation(SolidColorBrush.ColorProperty, bellFlash);
        }
    }

    private void TrayButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SessionActivityTrayViewModel vm)
        {
            return;
        }

        if (vm.IsOpen)
        {
            vm.IsOpen = false;
            e.Handled = true;
        }
    }
}
