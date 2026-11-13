using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitLord.VRModInstaller.Core.Notifications;

/// <summary>
/// Stable notification identifiers. The UI maps these to localized titles and
/// messages so switching language relocalizes older activity too.
/// </summary>
public static class NotificationKinds
{
    public const string InstallCompleted = "installCompleted";
    public const string StableReleaseAvailable = "stableReleaseAvailable";
    public const string BetaReleaseAvailable = "betaReleaseAvailable";
    public const string HelperUpdateAvailable = "helperUpdateAvailable";
    public const string SteamIntegrationCompleted = "steamIntegrationCompleted";
    public const string WiVrnDesktopEntryIssue = "wivrnDesktopEntryIssue";
    public const string InstallFailed = "installFailed";
}

public sealed class AppNotification
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("read")]
    public bool Read { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>
    /// Structured identity for events that should only be announced once, such
    /// as a specific stable release or helper commit. It is persisted with the
    /// activity so reopening the panel does not recreate duplicates.
    /// </summary>
    [JsonPropertyName("dedupeKey")]
    public string? DedupeKey { get; set; }
}

/// <summary>
/// Small durable activity log. It is intentionally separate from settings and
/// install history: restoring visual preferences or clearing a filter must not
/// erase notifications.
/// </summary>
public sealed class NotificationStore
{
    public const int MaxEntries = 50;

    private readonly XdgPaths _paths;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public NotificationStore(XdgPaths paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<AppNotification> Load()
    {
        try
        {
            if (!File.Exists(_paths.NotificationsFile))
            {
                return Array.Empty<AppNotification>();
            }

            var notifications = JsonSerializer.Deserialize<List<AppNotification>>(
                File.ReadAllText(_paths.NotificationsFile), _options) ?? new List<AppNotification>();
            return notifications
                .Where(IsUsable)
                .OrderByDescending(notification => notification.CreatedAt)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Array.Empty<AppNotification>();
        }
    }

    public void Save(IReadOnlyList<AppNotification> notifications)
    {
        Directory.CreateDirectory(_paths.AppState);
        var temp = _paths.NotificationsFile + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(notifications.Take(MaxEntries).ToList(), _options));
        File.Move(temp, _paths.NotificationsFile, overwrite: true);
    }

    private static bool IsUsable(AppNotification notification)
        => !string.IsNullOrWhiteSpace(notification.Id)
            && !string.IsNullOrWhiteSpace(notification.Kind)
            && notification.CreatedAt != default;
}
