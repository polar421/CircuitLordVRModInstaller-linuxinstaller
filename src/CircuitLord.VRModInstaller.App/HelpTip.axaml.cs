using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitLord.VRModInstaller.Appearance;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// A small contextual help affordance. The text is supplied by the view so it
/// follows the same live localization bindings as the surrounding labels.
/// </summary>
public partial class HelpTip : UserControl
{
    private static HelpTip? _openTip;

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<HelpTip, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> BodyProperty =
        AvaloniaProperty.Register<HelpTip, string>(nameof(Body), string.Empty);

    public static readonly StyledProperty<PlacementMode> TipPlacementProperty =
        AvaloniaProperty.Register<HelpTip, PlacementMode>(
            nameof(TipPlacement), PlacementMode.BottomEdgeAlignedLeft);

    public HelpTip()
    {
        InitializeComponent();
        HelpPopup.PlacementTarget = HelpButton;
        HelpPopup.Closed += HelpPopup_Closed;
        UpdateContent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public PlacementMode TipPlacement
    {
        get => GetValue(TipPlacementProperty);
        set => SetValue(TipPlacementProperty, value);
    }

    public bool IsOpen => HelpPopup.IsOpen;

    /// <summary>Closes whichever help popover is currently open.</summary>
    public static bool CloseOpen()
    {
        var tip = _openTip;
        if (tip is null || !tip.HelpPopup.IsOpen)
        {
            _openTip = null;
            return false;
        }

        tip.HelpPopup.IsOpen = false;
        _openTip = null;
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == BodyProperty)
        {
            UpdateContent();
        }
        else if (change.Property == TipPlacementProperty)
        {
            HelpPopup.Placement = TipPlacement;
        }
        else if (change.Property == IsVisibleProperty && !IsVisible)
        {
            ClosePopup();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClosePopup();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateContent()
    {
        HelpTitle.Text = Title ?? string.Empty;
        HelpBody.Text = Body ?? string.Empty;
        var accessibleName = string.IsNullOrWhiteSpace(Title) ? "?" : Title;
        AutomationProperties.SetName(HelpButton, accessibleName);
        ToolTip.SetTip(HelpButton, string.IsNullOrWhiteSpace(Title) ? null : Title);
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (HelpPopup.IsOpen)
        {
            ClosePopup();
            return;
        }

        _openTip?.ClosePopup();
        _openTip = this;

        var factor = AppearanceManager.ScaleFactor;
        HelpPopupScale.LayoutTransform = new ScaleTransform(factor, factor);
        var owner = TopLevel.GetTopLevel(this);
        if (owner is not null && owner.ClientSize.Width > 0)
        {
            var available = (owner.ClientSize.Width - 32) / factor;
            HelpPopupPanel.Width = Math.Clamp(available, 240, 300);
        }
        HelpPopup.Placement = TipPlacement;
        HelpPopup.IsOpen = true;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => ClosePopup();

    private void ClosePopup()
    {
        if (HelpPopup.IsOpen)
        {
            HelpPopup.IsOpen = false;
        }
        if (ReferenceEquals(_openTip, this))
        {
            _openTip = null;
        }
    }

    private void HelpPopup_Closed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_openTip, this))
        {
            _openTip = null;
        }
    }
}
