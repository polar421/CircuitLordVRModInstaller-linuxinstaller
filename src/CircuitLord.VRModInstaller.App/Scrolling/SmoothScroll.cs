using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CircuitLord.VRModInstaller.Appearance;

namespace CircuitLord.VRModInstaller.Scrolling;

/// <summary>
/// Adds subtle easing to discrete mouse-wheel scrolling while leaving native
/// touchpad, keyboard and scrollbar input untouched. ScrollViewers opt in
/// through the application style, so every current and future scroll area uses
/// the same behavior without per-page input handlers.
/// </summary>
public sealed class SmoothScroll
{
    // Match the distance of one conventional wheel notch in Avalonia's native
    // ScrollContentPresenter (50 DIPs), but distribute it over a few frames.
    private const double WheelStep = 50.0;
    private const double FollowSeconds = 0.055;
    private const double SnapPixels = 0.35;
    private const double EdgeEpsilon = 0.5;

    private static readonly Dictionary<ScrollViewer, ScrollState> Active = new();
    private static readonly ConditionalWeakTable<ScrollViewer, object> Attached = new();
    private static readonly HashSet<TopLevel> FrameScheduled = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    static SmoothScroll()
    {
        // If reduced motion is switched on while a wheel animation is running,
        // stop decorating the remaining movement immediately.
        AppearanceManager.MotionChanged += (_, _) =>
        {
            if (!AppearanceManager.AnimationsEnabled)
            {
                foreach (var viewer in Active.Keys.ToArray())
                {
                    Cancel(viewer);
                }
            }
        };

        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                Attach(viewer);
            }
            else
            {
                Detach(viewer);
            }
        });
    }

    /// <summary>
    /// Set by the application-wide ScrollViewer style. Keeping this attached
    /// property means popup roots, templated ListBox/TextBox scroll viewers and
    /// future views are covered automatically.
    /// </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScroll, ScrollViewer, bool>("IsEnabled");

    public static bool GetIsEnabled(ScrollViewer viewer) => viewer.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer viewer, bool value)
        => viewer.SetValue(IsEnabledProperty, value);

    private static void Attach(ScrollViewer viewer)
    {
        if (Attached.TryGetValue(viewer, out _))
        {
            return;
        }

        // A weak lifecycle table avoids retaining controls (and popup visual
        // trees) after they leave the window. The handler itself is owned by
        // the ScrollViewer.
        Attached.Add(viewer, new object());
        viewer.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        viewer.DetachedFromVisualTree += OnViewerDetached;
    }

    private static void Detach(ScrollViewer viewer)
    {
        if (!Attached.TryGetValue(viewer, out _))
        {
            return;
        }

        viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
        viewer.DetachedFromVisualTree -= OnViewerDetached;
        Attached.Remove(viewer);
        Cancel(viewer);
    }

    private static void OnViewerDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            Cancel(viewer);
        }
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !ShouldSmooth(e))
        {
            return;
        }

        var target = FindTarget(e.Source as Visual, e.Delta.Y);
        if (target.Viewer is null || target.Viewer != viewer)
        {
            // A nested scroll area owns this event. Its own tunnel handler will
            // apply the animation, or a boundary blocker will mark it handled.
            return;
        }

        if (target.Blocked)
        {
            // The inner view explicitly disables scroll chaining and is already
            // at its edge. Keep the parent from unexpectedly moving.
            e.Handled = true;
            return;
        }

        var max = MaxOffset(viewer);
        var currentTarget = GetTarget(viewer);
        var next = Math.Clamp(currentTarget - e.Delta.Y * WheelStep, 0, max);
        if (Math.Abs(next - currentTarget) < 0.01)
        {
            return;
        }

        // If the pointer has moved on to another scroll area, let that input own
        // the motion instead of leaving a previous parent animation running.
        CancelOtherAnimations(viewer);
        var state = GetOrCreateState(viewer);
        state.TargetY = next;
        ScheduleFrame(viewer);
        e.Handled = true;
    }

    private static bool ShouldSmooth(PointerWheelEventArgs e)
    {
        // Reduced motion and explicit system preferences use Avalonia's native,
        // immediate scrolling. The application scrollbar and keyboard paths are
        // never routed through this method.
        if (!AppearanceManager.AnimationsEnabled
            || e.Pointer.Type != PointerType.Mouse
            || e.KeyModifiers != KeyModifiers.None
            || Math.Abs(e.Delta.Y) < 0.999)
        {
            return false;
        }

        // Touchpads usually emit fractional/high-resolution deltas. Leave those
        // in Avalonia's native input path so the compositor's precision and
        // inertia are not replaced by a fixed-distance animation.
        return Math.Abs(e.Delta.Y - Math.Round(e.Delta.Y)) < 0.001;
    }

    private static ScrollTarget FindTarget(Visual? source, double deltaY)
    {
        for (var current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is not ScrollViewer viewer)
            {
                continue;
            }

            var max = MaxOffset(viewer);
            if (max <= EdgeEpsilon)
            {
                continue;
            }

            var target = GetTarget(viewer);
            var proposed = Math.Clamp(target - deltaY * WheelStep, 0, max);
            if (Math.Abs(proposed - target) > 0.01)
            {
                return new ScrollTarget(viewer, blocked: false);
            }

            if (!ScrollViewer.GetIsScrollChainingEnabled(viewer))
            {
                return new ScrollTarget(viewer, blocked: true);
            }
        }

        return default;
    }

    private static double MaxOffset(ScrollViewer viewer)
        => Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);

    private static double GetTarget(ScrollViewer viewer)
        => Active.TryGetValue(viewer, out var state) ? state.TargetY : viewer.Offset.Y;

    private static ScrollState GetOrCreateState(ScrollViewer viewer)
    {
        if (Active.TryGetValue(viewer, out var state))
        {
            return state;
        }

        state = new ScrollState
        {
            TargetY = viewer.Offset.Y,
            LastAppliedY = viewer.Offset.Y,
            LastFrame = Clock.Elapsed,
            TopLevel = TopLevel.GetTopLevel(viewer),
        };
        viewer.PropertyChanged += OnOffsetChanged;
        Active[viewer] = state;
        return state;
    }

    private static void OnOffsetChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ScrollViewer.OffsetProperty
            || sender is not ScrollViewer viewer
            || !Active.TryGetValue(viewer, out var state)
            || state.Applying)
        {
            return;
        }

        var current = viewer.Offset.Y;
        if (Math.Abs(current - state.LastAppliedY) > 0.05)
        {
            // Scrollbar dragging, touchpad gestures, keyboard paging and focus
            // bring-into-view all set Offset directly. Stop immediately so this
            // animation cannot fight the user's input.
            Cancel(viewer);
        }
    }

    private static void ScheduleFrame(ScrollViewer viewer)
    {
        var top = TopLevel.GetTopLevel(viewer);
        if (top is null || !FrameScheduled.Add(top))
        {
            return;
        }

        top.RequestAnimationFrame(_ => OnFrame(top));
    }

    private static void OnFrame(TopLevel top)
    {
        FrameScheduled.Remove(top);
        if (Active.Count == 0)
        {
            return;
        }

        var now = Clock.Elapsed;
        var hadActiveViewer = false;

        foreach (var (viewer, state) in Active.ToArray())
        {
            if (state.TopLevel != top)
            {
                continue;
            }
            hadActiveViewer = true;

            if (!viewer.IsAttachedToVisualTree())
            {
                Cancel(viewer);
                continue;
            }

            var elapsed = (now - state.LastFrame).TotalSeconds;
            state.LastFrame = now;
            // A slow render frame should still converge rather than jump with a
            // huge step after a long frame.
            var dt = Math.Clamp(elapsed, 0.001, 0.033);
            var max = MaxOffset(viewer);
            state.TargetY = Math.Clamp(state.TargetY, 0, max);
            var current = viewer.Offset.Y;
            var remaining = state.TargetY - current;

            if (Math.Abs(remaining) <= SnapPixels)
            {
                state.Applying = true;
                viewer.SetCurrentValue(
                    ScrollViewer.OffsetProperty,
                    new Vector(viewer.Offset.X, state.TargetY));
                state.Applying = false;
                state.LastAppliedY = viewer.Offset.Y;
                Cancel(viewer);
                continue;
            }

            var factor = 1 - Math.Exp(-dt / FollowSeconds);
            var next = current + remaining * factor;
            if (Math.Abs(state.TargetY - next) <= SnapPixels)
            {
                next = state.TargetY;
            }

            state.Applying = true;
            viewer.SetCurrentValue(
                ScrollViewer.OffsetProperty,
                new Vector(viewer.Offset.X, next));
            state.Applying = false;
            state.LastAppliedY = viewer.Offset.Y;

            if (Math.Abs(state.LastAppliedY - state.TargetY) <= 0.01)
            {
                Cancel(viewer);
            }
        }

        if (hadActiveViewer && Active.Values.Any(state => state.TopLevel == top))
        {
            ScheduleFrame(top);
        }
    }

    private static void ScheduleFrame(TopLevel top)
    {
        if (!FrameScheduled.Add(top))
        {
            return;
        }

        top.RequestAnimationFrame(_ => OnFrame(top));
    }

    private static void CancelOtherAnimations(ScrollViewer keep)
    {
        foreach (var viewer in Active.Keys.ToArray())
        {
            if (!ReferenceEquals(viewer, keep))
            {
                Cancel(viewer);
            }
        }
    }

    private static void Cancel(ScrollViewer viewer)
    {
        if (!Active.Remove(viewer, out var state))
        {
            return;
        }

        viewer.PropertyChanged -= OnOffsetChanged;
    }

    private readonly struct ScrollTarget
    {
        public ScrollTarget(ScrollViewer viewer, bool blocked)
        {
            Viewer = viewer;
            Blocked = blocked;
        }

        public ScrollViewer? Viewer { get; }

        public bool Blocked { get; }
    }

    private sealed class ScrollState
    {
        public double TargetY;
        public double LastAppliedY;
        public TimeSpan LastFrame;
        public bool Applying;
        public TopLevel? TopLevel;
    }
}
