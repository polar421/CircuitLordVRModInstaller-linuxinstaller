using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;

namespace CircuitLord.VRModInstaller.Appearance;

/// <summary>
/// Shared opacity and transform transitions for modal overlays. The dialog's
/// layout is never animated; only its visual opacity and render transform are.
/// </summary>
internal static class ModalTransitions
{
    private const string OpenTransform = "scale(1) translate(0px, 0px)";
    private const string InitialTransform = "scale(0.96) translate(0px, 8px)";
    private const string ClosingTransform = "scale(0.985) translate(0px, 5px)";

    public static void Configure(
        Control backdrop,
        Control dialog,
        bool animate,
        TimeSpan backdropDuration,
        TimeSpan dialogDuration,
        TimeSpan transformDuration)
    {
        if (!animate)
        {
            backdrop.Transitions = null;
            dialog.Transitions = null;
            return;
        }

        var easing = new CubicEaseOut();
        backdrop.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = backdropDuration,
                Easing = easing,
            },
        };
        dialog.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = dialogDuration,
                Easing = easing,
            },
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = transformDuration,
                Easing = easing,
            },
        };
    }

    public static void SetInitial(Control backdrop, Control dialog)
    {
        backdrop.Opacity = 0;
        dialog.Opacity = 0;
        dialog.RenderTransform = TransformOperations.Parse(InitialTransform);
    }

    public static void ApplyOpen(Control backdrop, Control dialog)
    {
        backdrop.Opacity = 1;
        dialog.Opacity = 1;
        dialog.RenderTransform = TransformOperations.Parse(OpenTransform);
    }

    public static void ApplyClosed(Control backdrop, Control dialog)
    {
        backdrop.Opacity = 0;
        dialog.Opacity = 0;
        dialog.RenderTransform = TransformOperations.Parse(ClosingTransform);
    }

    public static void ResetHidden(Control backdrop, Control dialog)
    {
        backdrop.Opacity = 0;
        dialog.Opacity = 1;
        dialog.RenderTransform = TransformOperations.Parse(OpenTransform);
    }
}
