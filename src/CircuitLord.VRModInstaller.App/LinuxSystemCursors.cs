using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CircuitLord.VRModInstaller;

/// <summary>
/// Avalonia 12.1 does not follow the desktop's Xcursor theme on Linux. Its
/// Wayland backend loads the generic "default" theme at 24 px, while the X11
/// backend uses X11's legacy cursor font. This bridge resolves the user's
/// configured theme and feeds the actual themed images through Avalonia's
/// normal bitmap-cursor path. The platform cursor factory remains the fallback
/// for cursor types the theme does not provide.
/// </summary>
internal static class LinuxSystemCursors
{
    private const string X11Library = "libX11.so.6";

    public static void Install()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var inner = AvaloniaServices.GetCursorFactory();
            if (inner is null || inner is CursorFactoryProxy)
            {
                return;
            }

            var settings = CursorThemeSettings.Detect();
            if (!CursorImageLoader.CanLoad(settings))
            {
                Trace.WriteLine("System cursor theme unavailable; keeping Avalonia's native cursors.");
                return;
            }

            AvaloniaServices.SetCursorFactory(CursorFactoryProxy.Create(inner, settings));

            Trace.WriteLine(
                $"Using desktop cursor theme '{settings.Theme ?? "(default)"}' at {settings.Size}px.");
        }
        catch (Exception ex)
        {
            // Cursor theming must never prevent the installer from starting.
            Trace.WriteLine($"System cursor integration unavailable: {ex.Message}");
        }
    }
}

/// <summary>
/// Avalonia marks its service locator and cursor API as private in the reference
/// assembly. The members are public in the runtime assembly; reflection lets an
/// application bridge them without taking a dependency on Avalonia internals.
/// </summary>
internal static class AvaloniaServices
{
    private static readonly PropertyInfo? CurrentProperty =
        typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Public | BindingFlags.Static);

    private static readonly PropertyInfo? CurrentMutableProperty =
        typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static);

    public static ICursorFactory? GetCursorFactory()
    {
        var resolver = CurrentProperty?.GetValue(null);
        if (resolver is null)
        {
            return null;
        }

        var getService = resolver.GetType()
            .GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance);
        return getService?.Invoke(resolver, [typeof(ICursorFactory)]) as ICursorFactory;
    }

    public static void SetCursorFactory(ICursorFactory factory)
    {
        if (CurrentMutableProperty?.GetValue(null) is not AvaloniaLocator locator)
        {
            throw new InvalidOperationException("Avalonia's service locator is unavailable.");
        }

        var bind = typeof(AvaloniaLocator)
            .GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance)?
            .MakeGenericMethod(typeof(ICursorFactory))
            ?? throw new MissingMethodException(nameof(AvaloniaLocator), "Bind");
        var registration = bind.Invoke(locator, null)
            ?? throw new InvalidOperationException("Avalonia service registration failed.");
        var toConstant = registration.GetType()
            .GetMethod("ToConstant", BindingFlags.Public | BindingFlags.Instance)?
            .MakeGenericMethod(typeof(ICursorFactory))
            ?? throw new MissingMethodException(registration.GetType().Name, "ToConstant");
        toConstant.Invoke(registration, [factory]);
    }
}

internal sealed record CursorThemeSettings(string? Theme, int Size)
{
    private const string X11Library = "libX11.so.6";
    private const int DefaultSize = 24;
    private const int MinimumSize = 8;
    private const int MaximumSize = 256;

    public static CursorThemeSettings Detect()
    {
        var theme = Clean(Environment.GetEnvironmentVariable("XCURSOR_THEME"));
        var size = ParseSize(Environment.GetEnvironmentVariable("XCURSOR_SIZE"));

        var desktops = string.Join(
            ':',
            Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
            Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"))
            .ToUpperInvariant();

        if (desktops.Contains("KDE", StringComparison.Ordinal)
            || desktops.Contains("PLASMA", StringComparison.Ordinal))
        {
            var kde = ReadKdeSettings();
            theme ??= kde.Theme;
            size ??= kde.Size;
        }
        else if (IsGnomeFamily(desktops))
        {
            theme ??= ReadGSettings("cursor-theme");
            size ??= ParseSize(ReadGSettings("cursor-size"));
        }

        // X11 exposes the same cursor settings through the X resource database.
        // This also covers desktops that do not publish a standard environment
        // variable (KDE is a common example).
        if (theme is null || size is null)
        {
            var resources = ReadXResources();
            theme ??= resources.Theme;
            size ??= resources.Size;
        }

        return new CursorThemeSettings(theme, NormalizeSize(size));
    }

    private static bool IsGnomeFamily(string desktops) =>
        desktops.Contains("GNOME", StringComparison.Ordinal)
        || desktops.Contains("UNITY", StringComparison.Ordinal)
        || desktops.Contains("CINNAMON", StringComparison.Ordinal)
        || desktops.Contains("MATE", StringComparison.Ordinal)
        || desktops.Contains("BUDGIE", StringComparison.Ordinal)
        || desktops.Contains("PANTHEON", StringComparison.Ordinal);

    private static CursorThemeSettings ReadKdeSettings()
    {
        string? theme = null;
        int? size = null;

        foreach (var path in KdeConfigPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var values = ReadIniSection(path, "Mouse");
                if (theme is null
                    && values.TryGetValue("cursorTheme", out var configuredTheme))
                {
                    theme = Clean(configuredTheme);
                }
                if (size is null
                    && values.TryGetValue("cursorSize", out var configuredSize))
                {
                    size = ParseSize(configuredSize);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A missing or unreadable preference file is not fatal; the
                // remaining sources and Avalonia's fallback still work.
            }
        }

        return new CursorThemeSettings(theme, NormalizeSize(size));
    }

    private static IEnumerable<string> KdeConfigPaths()
    {
        var configHome = Clean(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return Path.Combine(configHome, "kcminputrc");
        yield return Path.Combine(configHome, "kdedefaults", "kcminputrc");

        var configDirs = (Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") ?? "/etc/xdg")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in configDirs)
        {
            yield return Path.Combine(directory, "kcminputrc");
            yield return Path.Combine(directory, "kdedefaults", "kcminputrc");
        }
    }

    private static Dictionary<string, string> ReadIniSection(string path, string sectionName)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = string.Equals(line[1..^1].Trim(), sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string? ReadGSettings(string key)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gsettings",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("get");
            process.StartInfo.ArgumentList.Add("org.gnome.desktop.interface");
            process.StartInfo.ArgumentList.Add(key);

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(750))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (!output.Wait(500))
            {
                return null;
            }

            return Clean(output.Result.Trim().Trim('\'', '"'));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static (string? Theme, int? Size) ReadXResources()
    {
        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return (null, null);
            }

            return (ReadXResource(display, "theme"), ParseSize(ReadXResource(display, "size")));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return (null, null);
        }
        finally
        {
            if (display != IntPtr.Zero)
            {
                XCloseDisplay(display);
            }
        }
    }

    private static string? ReadXResource(IntPtr display, string resource)
    {
        var value = XGetDefault(display, "Xcursor", resource);
        return value == IntPtr.Zero ? null : Clean(Marshal.PtrToStringAnsi(value));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParseSize(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
        && size > 0
            ? size
            : null;

    private static int NormalizeSize(int? size) =>
        Math.Clamp(size ?? DefaultSize, MinimumSize, MaximumSize);

    [DllImport(X11Library)]
    private static extern IntPtr XOpenDisplay(string? display);

    [DllImport(X11Library)]
    private static extern IntPtr XGetDefault(IntPtr display, string program, string option);

    [DllImport(X11Library)]
    private static extern int XCloseDisplay(IntPtr display);
}

/// <summary>
/// Avalonia marks ICursorFactory as non-implementable at compile time, although the
/// runtime contract is only the two cursor methods. A DispatchProxy delegates those
/// calls without forcing the app to fork or reimplement the platform backend.
/// </summary>
internal class CursorFactoryProxy : DispatchProxy
{
    private static readonly MethodInfo GetCursorMethod =
        typeof(ICursorFactory).GetMethod("GetCursor", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(ICursorFactory), "GetCursor");

    private static readonly MethodInfo CreateCursorMethod =
        typeof(ICursorFactory).GetMethod("CreateCursor", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(ICursorFactory), "CreateCursor");

    private ICursorFactory _inner = null!;
    private CursorThemeSettings _settings = null!;

    public static ICursorFactory Create(ICursorFactory inner, CursorThemeSettings settings)
    {
        var proxy = DispatchProxy.Create<ICursorFactory, CursorFactoryProxy>();
        var implementation = (CursorFactoryProxy)(object)proxy;
        implementation._inner = inner;
        implementation._settings = settings;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetCursor"
            && args is { Length: 1 }
            && args[0] is StandardCursorType cursorType)
        {
            if (cursorType != StandardCursorType.None)
            {
                try
                {
                    var themed = CursorImageLoader.CreateCursor(
                        cursorType,
                        _settings,
                        CreateBitmapCursor);
                    if (themed is not null)
                    {
                        return themed;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Trace.WriteLine($"Could not load themed cursor '{cursorType}': {ex.Message}");
                }
            }

            return GetCursorMethod.Invoke(_inner, [cursorType]);
        }

        if (targetMethod?.Name == "CreateCursor"
            && args is { Length: 2 }
            && args[0] is Bitmap bitmap
            && args[1] is PixelPoint hotSpot)
        {
            return CreateBitmapCursor(bitmap, hotSpot);
        }

        throw new MissingMethodException(targetMethod?.DeclaringType?.FullName, targetMethod?.Name);
    }

    private ICursorImpl CreateBitmapCursor(Bitmap bitmap, PixelPoint hotSpot) =>
        (ICursorImpl)CreateCursorMethod.Invoke(_inner, [bitmap, hotSpot])!;
}

internal static class CursorImageLoader
{
    private const string XcursorLibrary = "libXcursor.so.1";
    private static readonly IReadOnlyDictionary<StandardCursorType, string[]> CursorNames =
        new Dictionary<StandardCursorType, string[]>
        {
            [StandardCursorType.Arrow] = ["default", "left_ptr"],
            [StandardCursorType.Ibeam] = ["text", "xterm"],
            [StandardCursorType.Wait] = ["wait", "watch"],
            [StandardCursorType.Cross] = ["crosshair", "cross"],
            [StandardCursorType.UpArrow] = ["up_arrow", "sb_up_arrow"],
            [StandardCursorType.SizeWestEast] = ["ew-resize", "col-resize", "sb_h_double_arrow"],
            [StandardCursorType.SizeNorthSouth] = ["ns-resize", "row-resize", "sb_v_double_arrow"],
            [StandardCursorType.SizeAll] = ["all-scroll", "fleur"],
            [StandardCursorType.No] = ["not-allowed", "crossed_circle"],
            [StandardCursorType.Hand] = ["pointer", "hand2", "pointing_hand"],
            [StandardCursorType.AppStarting] = ["progress", "left_ptr_watch"],
            [StandardCursorType.Help] = ["help", "question_arrow"],
            [StandardCursorType.TopSide] = ["top_side", "n-resize"],
            [StandardCursorType.BottomSide] = ["bottom_side", "s-resize"],
            [StandardCursorType.LeftSide] = ["left_side", "w-resize"],
            [StandardCursorType.RightSide] = ["right_side", "e-resize"],
            [StandardCursorType.TopLeftCorner] = ["top_left_corner", "nw-resize"],
            [StandardCursorType.TopRightCorner] = ["top_right_corner", "ne-resize"],
            [StandardCursorType.BottomLeftCorner] = ["bottom_left_corner", "sw-resize"],
            [StandardCursorType.BottomRightCorner] = ["bottom_right_corner", "se-resize"],
            [StandardCursorType.DragMove] = ["grabbing", "dnd-move"],
            [StandardCursorType.DragCopy] = ["copy", "dnd-copy"],
            [StandardCursorType.DragLink] = ["alias", "dnd-link"],
        };

    public static bool CanLoad(CursorThemeSettings settings)
    {
        foreach (var name in new[] { "default", "left_ptr" })
        {
            var images = XcursorLibraryLoadImages(name, settings.Theme, settings.Size);
            if (images == IntPtr.Zero)
            {
                continue;
            }

            XcursorImagesDestroy(images);
            return true;
        }

        return false;
    }

    public static ICursorImpl? CreateCursor(
        StandardCursorType cursorType,
        CursorThemeSettings settings,
        Func<Bitmap, PixelPoint, ICursorImpl> createBitmapCursor)
    {
        if (!CursorNames.TryGetValue(cursorType, out var names))
        {
            return null;
        }

        foreach (var name in names)
        {
            var images = XcursorLibraryLoadImages(name, settings.Theme, settings.Size);
            if (images == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var collection = Marshal.PtrToStructure<XcursorImages>(images);
                if (collection.Count <= 0 || collection.Images == IntPtr.Zero)
                {
                    continue;
                }

                var imageHandle = Marshal.ReadIntPtr(collection.Images);
                if (imageHandle == IntPtr.Zero)
                {
                    continue;
                }

                var image = Marshal.PtrToStructure<XcursorImage>(imageHandle);
                if (image.Width is 0 or > 32767
                    || image.Height is 0 or > 32767
                    || image.Pixels == IntPtr.Zero)
                {
                    continue;
                }

                var width = (int)image.Width;
                var height = (int)image.Height;
                var hotspot = new PixelPoint(
                    Math.Min((int)image.XHotspot, width - 1),
                    Math.Min((int)image.YHotspot, height - 1));

                using var bitmap = new Bitmap(
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul,
                    image.Pixels,
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    checked(width * 4));

                return createBitmapCursor(bitmap, hotspot);
            }
            finally
            {
                XcursorImagesDestroy(images);
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XcursorImages
    {
        public int Count;
        public IntPtr Images;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XcursorImage
    {
        public uint Version;
        public uint Size;
        public uint Width;
        public uint Height;
        public uint XHotspot;
        public uint YHotspot;
        public uint Delay;
        public IntPtr Pixels;
    }

    [DllImport(XcursorLibrary)]
    private static extern IntPtr XcursorLibraryLoadImages(
        [MarshalAs(UnmanagedType.LPStr)] string library,
        [MarshalAs(UnmanagedType.LPStr)] string? theme,
        int size);

    [DllImport(XcursorLibrary)]
    private static extern void XcursorImagesDestroy(IntPtr images);
}
