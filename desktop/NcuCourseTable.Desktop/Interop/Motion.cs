using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace NcuCourseTable.Desktop.Interop;

/// <summary>
/// 弹簧动效引擎（WPF 版“Designing Fluid Interfaces”）。
///
/// 与 Storyboard 不同，弹簧永远从“当前屏幕值”出发，重定向时保留速度，
/// 因此动画可在任意时刻被抓住、反向，而不会跳变或撞墙。
/// 参数对应 Apple 的 damping / response：
///   - damping = 1.0 临界阻尼（默认，不超调）
///   - damping ≈ 0.8 仅在手势带速度时使用（甩动、拖动释放）
///   - response 为到达目标的速度（秒），不是固定时长。
/// 同时遵守 Windows 的“显示动画”设置：关闭时直接落到目标值。
/// </summary>
public static class Motion
{
    private sealed class SpringState
    {
        public DependencyProperty Property = null!;
        public double Target;
        public double Velocity;
        public double Damping;
        public double Response;
        public double Tolerance;
        public TimeSpan Last;
        public Action? Completed;
    }

    private static readonly Dictionary<(DependencyObject Target, DependencyProperty Property), SpringState> States = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static bool _hooked;

    /// <summary>系统关闭了 UI 动画 / 高对比度时，不再做位移与弹簧。</summary>
    public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast;

    public static bool IsAnimating(DependencyObject target, DependencyProperty property)
        => States.ContainsKey((target, property));

    /// <summary>
    /// 以弹簧方式把 <paramref name="property"/> 动画到 <paramref name="to"/>。
    /// 同一对象/属性再次调用时保留当前速度，实现可中断重定向。
    /// </summary>
    public static void SpringTo(DependencyObject target, DependencyProperty property, double to,
        double damping = 1.0, double response = 0.35, double velocity = 0,
        double tolerance = 0.001, Action? completed = null)
    {
        if (ReducedMotion || response <= 0)
        {
            target.SetCurrentValue(property, to);
            completed?.Invoke();
            return;
        }

        var key = (target, property);
        bool isNew = !States.TryGetValue(key, out SpringState? state);
        if (isNew)
        {
            state = new SpringState
            {
                Property = property,
                Last = TimeSpan.MinValue,
                Velocity = velocity,
            };
            States[key] = state;
        }
        else if (velocity != 0)
        {
            // 重定向时不覆盖已有速度，除非调用方明确给出手势速度
            state!.Velocity = velocity;
        }

        state!.Target = to;
        state.Damping = damping;
        state.Response = response;
        state.Tolerance = tolerance;
        state.Completed = completed;

        if (!_hooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _hooked = true;
        }
    }

    public static void Stop(DependencyObject target, DependencyProperty property)
        => States.Remove((target, property));

    /// <summary>透明度 + 位移入场；reduced-motion 下直接呈现最终值。</summary>
    public static void FadeSlideIn(UIElement element, double offsetX = 0, double offsetY = 10,
        double response = 0.34, double damping = 1.0)
    {
        var translate = new TranslateTransform(offsetX, offsetY);
        element.RenderTransform = translate;
        element.Opacity = 0;

        SpringTo(translate, TranslateTransform.XProperty, 0, damping, response, tolerance: 0.1);
        SpringTo(translate, TranslateTransform.YProperty, 0, damping, response, tolerance: 0.1);
        SpringTo(element, UIElement.OpacityProperty, 1, damping, response * 0.9, tolerance: 0.002);
    }

    /// <summary>缩放 + 透明度入场（弹窗 / 卡片材质出现）。</summary>
    public static void ScaleFadeIn(FrameworkElement element, double fromScale = 0.97,
        double response = 0.36, double damping = 1.0)
    {
        var scale = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        element.Opacity = 0;

        SpringTo(scale, ScaleTransform.ScaleXProperty, 1, damping, response, tolerance: 0.0005);
        SpringTo(scale, ScaleTransform.ScaleYProperty, 1, damping, response, tolerance: 0.0005);
        SpringTo(element, UIElement.OpacityProperty, 1, damping, response * 0.85, tolerance: 0.002);
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (States.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
            return;
        }

        TimeSpan now = Clock.Elapsed;

        List<((DependencyObject, DependencyProperty), Action?)>? completed = null;
        List<(DependencyObject, DependencyProperty)>? finished = null;

        foreach (var key in new List<(DependencyObject, DependencyProperty)>(States.Keys))
        {
            (DependencyObject target, DependencyProperty property) = key;
            SpringState state = States[key];

            if (state.Last == TimeSpan.MinValue)
            {
                state.Last = now - TimeSpan.FromSeconds(1.0 / 60.0);
            }

            double dt = (now - state.Last).TotalSeconds;
            state.Last = now;
            if (dt <= 0) continue;
            dt = Math.Min(dt, 0.1);

            if (property.PropertyType != typeof(double))
            {
                finished ??= new();
                finished.Add(key);
                continue;
            }
            double x = (double)target.GetValue(property);

            double v = state.Velocity;
            double omega = 2.0 * Math.PI / state.Response;
            double stiffness = omega * omega;
            double drag = 2.0 * state.Damping * omega;

            // 固定小步长积分，掉帧时也保持稳定
            int steps = Math.Max(1, (int)Math.Ceiling(dt / 0.008));
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                double a = -stiffness * (x - state.Target) - drag * v;
                v += a * h;
                x += v * h;
            }

            target.SetCurrentValue(property, x);
            state.Velocity = v;

            if (Math.Abs(x - state.Target) <= state.Tolerance &&
                Math.Abs(v) <= state.Tolerance * 20.0)
            {
                target.SetCurrentValue(property, state.Target);
                finished ??= new();
                finished.Add(key);
                if (state.Completed is not null)
                {
                    completed ??= new();
                    completed.Add((key, state.Completed));
                }
            }
        }

        if (finished is not null)
        {
            foreach (var key in finished) States.Remove(key);
        }
        if (completed is not null)
        {
            foreach (var (_, callback) in completed) callback?.Invoke();
        }
        if (States.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }
}
