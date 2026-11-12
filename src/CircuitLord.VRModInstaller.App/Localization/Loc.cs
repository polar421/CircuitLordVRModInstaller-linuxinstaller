using System;
using System.Collections.Generic;
using Avalonia.Markup.Xaml;

namespace CircuitLord.VRModInstaller.Localization;

/// <summary>
/// Tiny string catalogue for the interface chrome. Backend and git output stay
/// in English because it is technical output, not installer copy.
/// </summary>
public static class Loc
{
    public static string Language { get; private set; } = "en";

    public static void Use(string? language) =>
        Language = string.Equals(language, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";

    public static string Get(string key)
    {
        var table = Language == "de" ? German : English;
        return table.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(Get(key), args);

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["chooseGame"] = "Choose a game",
        ["notAvailableLinux"] = "Not available on Linux",
        ["noBackend"] = "No backend",
        ["checkingInstallation"] = "Checking installation...",
        ["linuxBackend"] = "Linux backend",
        ["updateHelper"] = "Update helper",
        ["refresh"] = "Refresh",
        ["findTitanfall"] = "Find Titanfall 2",
        ["runBackendCheck"] = "Run backend check",
        ["release"] = "Release",
        ["install"] = "Install",
        ["play"] = "Play",
        ["playDescription"] = "Launching uses the installed native tf2vr launcher, the same one the desktop entry and Steam shortcut use.",
        ["launchVr"] = "Launch in VR",
        ["integrations"] = "Integrations",
        ["repairDesktop"] = "Repair WiVRn entry",
        ["removeShortcut"] = "Remove shortcut",
        ["addSteam"] = "Add to Steam",
        ["openXrUntouched"] = "OpenXR runtime left untouched",
        ["releaseNotes"] = "Release notes",
        ["openChangelog"] = "Open changelog",
        ["log"] = "Log",
        ["notInstalled"] = "Not installed",
        ["uninstall"] = "Uninstall",
        ["backToGames"] = "Back to games",
        ["minimize"] = "Minimize",
        ["close"] = "Close",
        ["settings"] = "Settings",
        ["settingsSubtitle"] = "Changes save immediately.",
        ["supportCircuitLord"] = "Support CircuitLord",
        ["supportTooltip"] = "Opens ko-fi.com/circuitlord in your browser",
        ["footerNote"] = "Made for the love of VR by CircuitLord",
        ["new"] = "NEW",
        ["changelogTitle"] = "Titanfall 2 VR changelog",
        ["changelogSubtitle"] = "From CircuitLord's official manifest",
        ["cancel"] = "Cancel",
        ["restartSteamAdd"] = "Restart Steam and Add",
        ["appearance"] = "Appearance",
        ["roundedCorners"] = "Rounded corners",
        ["roundedCornersDescription"] = "Round cards, buttons and overlays.",
        ["language"] = "Language",
        ["interfaceLanguage"] = "Interface language",
        ["languageRestart"] = "Changes apply after restart.",
        ["updates"] = "Updates",
        ["checkForUpdates"] = "Check for installer updates",
        ["checkForUpdatesDescription"] = "Ask GitHub for a newer release when the app starts.",
        ["updateHelperAutomatic"] = "Update the helper automatically",
        ["updateHelperAutomaticDescription"] = "Runs git pull for the tf2vr-linux checkout on startup.",
        ["checkNow"] = "Check now",
        ["openRelease"] = "Open release",
        ["updateChecksInfo"] = "Update checks never install anything automatically.",
        ["updatesNotChecked"] = "Updates not checked yet.",
        ["checkingUpdates"] = "Checking for installer updates...",
        ["updateCheckFailed"] = "Update check failed: {0}",
        ["updateAvailable"] = "Version {0} is available.",
        ["upToDate"] = "Up to date ({0}).",
        ["backendState"] = "Backend state: {0}",
        ["launcherVersionUnknown"] = "Launcher present; version unknown",
        ["updateReinstall"] = "Update / Reinstall",
        ["switchVersion"] = "Switch version",
        ["installedStatus"] = "Installed · {0}",
        ["readyStatus"] = "Ready to install through tf2vr-linux",
        ["uninstallTitle"] = "Uninstall Titanfall 2 VR?",
        ["uninstallText"] = "The backend removes the mod files, its launcher and Proton-TF2VR. Campaign saves and the normal Titanfall 2 installation are kept.",
        ["addSteamTitle"] = "Add Titanfall 2 VR to Steam?",
        ["addSteamText"] = "Steam needs to restart to install or update the library shortcut. The application will close Steam gracefully, update the shortcut and reopen the same Steam installation automatically. Existing games and shortcuts are preserved.",
        ["removeSteamTitle"] = "Remove the Titanfall 2 VR shortcut?",
        ["removeSteamText"] = "Steam will be closed gracefully, the Titanfall 2 VR shortcut removed, and Steam reopened. Other shortcuts are preserved.",
        ["restartSteamRemove"] = "Restart Steam and Remove",
    };

    private static readonly Dictionary<string, string> German = new(StringComparer.Ordinal)
    {
        ["chooseGame"] = "Spiel auswählen",
        ["notAvailableLinux"] = "Unter Linux nicht verfügbar",
        ["noBackend"] = "Kein Backend",
        ["checkingInstallation"] = "Installation wird geprüft...",
        ["linuxBackend"] = "Linux-Backend",
        ["updateHelper"] = "Helfer aktualisieren",
        ["refresh"] = "Aktualisieren",
        ["findTitanfall"] = "Titanfall 2 finden",
        ["runBackendCheck"] = "Backend-Prüfung starten",
        ["release"] = "Version",
        ["install"] = "Installieren",
        ["play"] = "Spielen",
        ["playDescription"] = "Der Start nutzt den installierten nativen tf2vr-Launcher – denselben wie der Desktop-Eintrag und die Steam-Verknüpfung.",
        ["launchVr"] = "In VR starten",
        ["integrations"] = "Integrationen",
        ["repairDesktop"] = "WiVRn-Eintrag reparieren",
        ["removeShortcut"] = "Verknüpfung entfernen",
        ["addSteam"] = "Zu Steam hinzufügen",
        ["openXrUntouched"] = "OpenXR-Runtime bleibt unverändert",
        ["releaseNotes"] = "Versionshinweise",
        ["openChangelog"] = "Changelog öffnen",
        ["log"] = "Protokoll",
        ["notInstalled"] = "Nicht installiert",
        ["uninstall"] = "Deinstallieren",
        ["backToGames"] = "Zurück zur Spielauswahl",
        ["minimize"] = "Minimieren",
        ["close"] = "Schließen",
        ["settings"] = "Einstellungen",
        ["settingsSubtitle"] = "Änderungen werden sofort gespeichert.",
        ["supportCircuitLord"] = "CircuitLord unterstützen",
        ["supportTooltip"] = "Öffnet ko-fi.com/circuitlord im Browser",
        ["footerNote"] = "Mit Liebe für VR von CircuitLord",
        ["new"] = "NEU",
        ["changelogTitle"] = "Titanfall 2 VR-Changelog",
        ["changelogSubtitle"] = "Aus CircuitLords offiziellem Manifest",
        ["cancel"] = "Abbrechen",
        ["restartSteamAdd"] = "Steam neu starten und hinzufügen",
        ["appearance"] = "Darstellung",
        ["roundedCorners"] = "Abgerundete Ecken",
        ["roundedCornersDescription"] = "Karten, Schaltflächen und Overlays abrunden.",
        ["language"] = "Sprache",
        ["interfaceLanguage"] = "Oberflächensprache",
        ["languageRestart"] = "Änderungen gelten nach einem Neustart.",
        ["updates"] = "Aktualisierungen",
        ["checkForUpdates"] = "Auf Installer-Aktualisierungen prüfen",
        ["checkForUpdatesDescription"] = "Beim Start bei GitHub nach einer neueren Version suchen.",
        ["updateHelperAutomatic"] = "Helfer automatisch aktualisieren",
        ["updateHelperAutomaticDescription"] = "Führt beim Start git pull für den tf2vr-linux-Checkout aus.",
        ["checkNow"] = "Jetzt prüfen",
        ["openRelease"] = "Version öffnen",
        ["updateChecksInfo"] = "Aktualisierungsprüfungen installieren nie automatisch etwas.",
        ["updatesNotChecked"] = "Aktualisierungen noch nicht geprüft.",
        ["checkingUpdates"] = "Installations-Aktualisierungen werden geprüft...",
        ["updateCheckFailed"] = "Aktualisierungsprüfung fehlgeschlagen: {0}",
        ["updateAvailable"] = "Version {0} ist verfügbar.",
        ["upToDate"] = "Auf dem neuesten Stand ({0}).",
        ["backendState"] = "Backend-Status: {0}",
        ["launcherVersionUnknown"] = "Launcher vorhanden; Version unbekannt",
        ["updateReinstall"] = "Aktualisieren / Neu installieren",
        ["switchVersion"] = "Version wechseln",
        ["installedStatus"] = "Installiert · {0}",
        ["readyStatus"] = "Bereit zur Installation über tf2vr-linux",
        ["uninstallTitle"] = "Titanfall 2 VR deinstallieren?",
        ["uninstallText"] = "Das Backend entfernt die Mod-Dateien, den Launcher und Proton-TF2VR. Kampagnenspeicher und die normale Titanfall-2-Installation bleiben erhalten.",
        ["addSteamTitle"] = "Titanfall 2 VR zu Steam hinzufügen?",
        ["addSteamText"] = "Steam muss neu gestartet werden, um die Bibliotheksverknüpfung zu installieren oder zu aktualisieren. Die Anwendung beendet Steam sauber, aktualisiert die Verknüpfung und öffnet dieselbe Steam-Installation automatisch wieder. Vorhandene Spiele und Verknüpfungen bleiben erhalten.",
        ["removeSteamTitle"] = "Titanfall 2 VR-Verknüpfung entfernen?",
        ["removeSteamText"] = "Steam wird sauber beendet, die Titanfall 2 VR-Verknüpfung entfernt und Steam wieder geöffnet. Andere Verknüpfungen bleiben erhalten.",
        ["restartSteamRemove"] = "Steam neu starten und entfernen",
    };
}

/// <summary>XAML helper: <c>Text="{loc:Tr Key=chooseGame}"</c>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
