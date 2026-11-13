using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Notifications;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller;

public sealed class NotificationItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required string TimeText { get; init; }
    public bool IsUnread { get; init; }
}

/// <summary>
/// Title-bar activity bell. Notifications are stored by the view model; this
/// control only localizes and renders them, so changing language also updates
/// older activity.
/// </summary>
public partial class NotificationCenterView : UserControl
{
    private InstallerViewModel? _vm;
    private bool _languageSubscribed;

    public NotificationCenterView()
    {
        InitializeComponent();
        NotificationPopup.PlacementTarget = NotificationButton;
    }

    public void Open(InstallerViewModel vm)
    {
        if (!ReferenceEquals(_vm, vm))
        {
            Unsubscribe();
            _vm = vm;
            vm.NotificationsChanged += NotificationsChanged;
        }
        SubscribeToLanguage();
        Refresh();
    }

    public void Close()
    {
        Unsubscribe();
        _vm = null;
        NotificationPopup.IsOpen = false;
    }

    public bool IsOpen => NotificationPopup.IsOpen;

    public bool HandleEscape()
    {
        if (!NotificationPopup.IsOpen)
        {
            return false;
        }
        NotificationPopup.IsOpen = false;
        return true;
    }

    public void ApplyScale()
    {
        var factor = AppearanceManager.ScaleFactor;
        NotificationPopupScale.LayoutTransform = new ScaleTransform(factor, factor);

        var owner = TopLevel.GetTopLevel(this);
        var width = owner?.ClientSize.Width ?? 800;
        var height = owner?.ClientSize.Height ?? 740;
        var availableWidth = width > 0 ? (width - 40) / factor : 360;
        var availableHeight = height > 0 ? (height - 80) / factor : 430;
        NotificationPopupPanel.Width = Math.Clamp(availableWidth, 280, 360);
        NotificationPopupPanel.MaxHeight = Math.Clamp(availableHeight, 220, 430);

        // A popup's placement is calculated from the pre-scale anchor. Reopen
        // it after the new transform so it stays attached to the bell when the
        // UI scale or window size changes while activity is open.
        if (NotificationPopup.IsOpen)
        {
            NotificationPopup.IsOpen = false;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_vm is not null && VisualRoot is not null)
                    {
                        NotificationPopup.PlacementTarget = NotificationButton;
                        NotificationPopup.IsOpen = true;
                    }
                },
                DispatcherPriority.Loaded);
        }
    }

    private void SubscribeToLanguage()
    {
        if (_languageSubscribed)
        {
            return;
        }
        Loc.LanguageChanged += LanguageChanged;
        _languageSubscribed = true;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.NotificationsChanged -= NotificationsChanged;
        }
        if (_languageSubscribed)
        {
            Loc.LanguageChanged -= LanguageChanged;
            _languageSubscribed = false;
        }
    }

    private void LanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Refresh);

    private void NotificationsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Refresh);

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        if (NotificationPopup.IsOpen)
        {
            NotificationPopup.IsOpen = false;
            return;
        }

        HelpTip.CloseOpen();
        Refresh();
        NotificationPopup.IsOpen = true;
        NotificationScroll.SetCurrentValue(ScrollViewer.OffsetProperty, default);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
        => NotificationPopup.IsOpen = false;

    private void MarkAllRead_Click(object? sender, RoutedEventArgs e)
        => _vm?.MarkAllNotificationsRead();

    private void Notification_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: NotificationItem item })
        {
            _vm?.MarkNotificationRead(item.Id);
        }
    }

    private void Refresh()
    {
        var vm = _vm;
        if (vm is null)
        {
            NotificationList.ItemsSource = Array.Empty<NotificationItem>();
            NotificationEmpty.IsVisible = true;
            UnreadBadge.IsVisible = false;
            MarkAllReadButton.IsVisible = false;
            return;
        }

        var unread = vm.UnreadNotificationCount;
        NotificationSubtitleText.Text = unread == 0
            ? Loc.Get("notificationCenterSubtitle")
            : Loc.Format("notificationUnreadCount", unread);
        UnreadBadge.IsVisible = unread > 0;
        UnreadCountText.Text = unread > 99 ? "99+" : unread.ToString(CultureInfo.CurrentCulture);
        MarkAllReadButton.IsVisible = unread > 0;

        var items = vm.Notifications
            .OrderByDescending(notification => notification.CreatedAt)
            .Select(CreateItem)
            .ToList();
        NotificationList.ItemsSource = items;
        NotificationEmpty.IsVisible = items.Count == 0;
    }

    private static NotificationItem CreateItem(AppNotification notification)
    {
        var version = string.IsNullOrWhiteSpace(notification.Version)
            ? Loc.Get("notAvailable")
            : notification.Version;
        var detail = string.IsNullOrWhiteSpace(notification.Detail)
            ? Loc.Get("notAvailable")
            : notification.Detail;

        var (title, message) = notification.Kind switch
        {
            NotificationKinds.InstallCompleted => (
                Loc.Get("notificationInstallCompletedTitle"),
                Loc.Format("notificationInstallCompletedBody", version)),
            NotificationKinds.StableReleaseAvailable => (
                Loc.Get("notificationStableAvailableTitle"),
                Loc.Format("notificationStableAvailableBody", version)),
            NotificationKinds.BetaReleaseAvailable => (
                Loc.Get("notificationBetaAvailableTitle"),
                Loc.Format("notificationBetaAvailableBody", version)),
            NotificationKinds.HelperUpdateAvailable => (
                Loc.Get("notificationHelperUpdateTitle"),
                Loc.Format("notificationHelperUpdateBody", detail)),
            NotificationKinds.SteamIntegrationCompleted => (
                Loc.Get("notificationSteamCompletedTitle"),
                Loc.Get("notificationSteamCompletedBody")),
            NotificationKinds.WiVrnDesktopEntryIssue => (
                Loc.Get("notificationWiVrnIssueTitle"),
                Loc.Get("notificationWiVrnIssueBody")),
            NotificationKinds.InstallFailed => (
                Loc.Get("notificationInstallFailedTitle"),
                Loc.Format("notificationInstallFailedBody", version, detail)),
            _ => (Loc.Get("notificationCenter"), notification.Detail ?? string.Empty),
        };

        return new NotificationItem
        {
            Id = notification.Id,
            Title = title,
            Message = message,
            TimeText = notification.CreatedAt.ToLocalTime().ToString("g"),
            IsUnread = !notification.Read,
        };
    }
}
