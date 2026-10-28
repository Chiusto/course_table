using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NcuCourseSource;

internal static class UiMotion
{
    public static void ScaleFadeIn(FrameworkElement element, double from = 0.96, double seconds = 0.3)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = 1;
            return;
        }
        var scale = new ScaleTransform(from, from);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        element.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
    }
}
