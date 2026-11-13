using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using CircuitLord.VRModInstaller.Appearance;
using CircuitLord.VRModInstaller.Core.Settings;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class GamePageTransitionScreenshotTests : HeadlessUiTest
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 740;
    private const int ChannelTolerance = 3;
    private const int MaxDifferentPixels = 96;

    private static readonly string[] ContentGroups =
    [
        "InstallerNav",
        "HeroContent",
        "InstalledVersionCard",
        "LatestStableCard",
        "LatestBetaCard",
        "ReleaseSection",
        "BackendSection",
        "ReleaseNotesSection",
        "OverviewDiagnosticsSection",
    ];

    [Theory]
    [InlineData("full", "en", true)]
    [InlineData("full", "en", false)]
    [InlineData("reduced", "en", true)]
    [InlineData("reduced", "en", false)]
    [InlineData("full", "de", true)]
    [InlineData("full", "de", false)]
    [InlineData("reduced", "de", true)]
    [InlineData("reduced", "de", false)]
    public Task TransitionMidpointSnapshotIsStable(string motion, string language, bool opening) => Dispatch(() =>
    {
        using var host = new TitanfallSnapshotHost(Home, motion, language);
        if (motion == "full")
        {
            host.SetMidpoint(opening);
        }
        else
        {
            // Reduced motion has no timed midpoint; its zero-duration endpoint
            // is the deterministic state to compare.
            host.SetReducedEndpoint(opening);
        }

        host.AssertSnapshot($"{motion}-{language}-{(opening ? "open" : "close")}.png");
    });

    private sealed class TitanfallSnapshotHost : IDisposable
    {
        private readonly MainWindow _window;

        public TitanfallSnapshotHost(TemporaryHome home, string motion, string language)
        {
            var store = new SettingsStore(home.Paths);
            var settings = store.Load();
            settings.Language = language;
            settings.Theme = "dark";
            settings.UiScale = 100;
            settings.MotionPreference = motion;
            settings.CheckForAppUpdates = false;
            settings.UpdateBackendAutomatically = false;
            settings.SetupWizardCompleted = true;
            store.Save(settings);
            Loc.Use(language);
            AppearanceManager.Initialize(settings);

            // A checkout with an invalid origin avoids cloning the real backend
            // and leaves the catalogue in a deterministic unavailable state.
            Directory.CreateDirectory(Path.Combine(home.Paths.AppData, "backend", ".git"));

            _window = new MainWindow
            {
                Width = WindowWidth,
                Height = WindowHeight,
            };
            _window.Show();
            _window.SetRenderScaling(1);
            HeadlessUi.Pump(250);

            var attribution = _window.FindControl<Border>("AttributionOverlay");
            if (attribution is not null)
            {
                attribution.IsVisible = false;
            }

            var viewModel = (InstallerViewModel)_window.DataContext!;
            WaitUntil(
                () => viewModel.CatalogueText != Loc.Get("catalogueLoading"),
                "backend initialization");
            HeadlessUi.Pump(100);
        }

        public void SetMidpoint(bool opening)
        {
            // Freeze the shared surface at its transition midpoint: both pages
            // are half-transparent with the incoming/outgoing 50% offsets.
            // Content groups are settled so the snapshot exercises the final
            // layout under that overlap rather than an arbitrary fade sample.
            var page = _window.FindControl<Grid>("InstallerPage")!;
            var gameScroll = _window.FindControl<ScrollViewer>("GameScroll")!;
            var footer = _window.FindControl<StackPanel>("GameFooter")!;
            var back = _window.FindControl<Button>("BackToGamesButton")!;
            var card = _window.FindControl<Button>("TitanfallCard")!;

            if (opening)
            {
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HeadlessUi.Pump(1200);
            }
            else
            {
                card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HeadlessUi.Pump(1200);
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HeadlessUi.Pump(700);
            }

            SetContentGroupsVisible();

            page.IsVisible = true;
            page.IsEnabled = opening;
            page.Transitions = null;
            page.Opacity = 0.5;
            page.RenderTransform = Translate(opening ? 4 : -3);

            gameScroll.IsVisible = true;
            gameScroll.Transitions = null;
            gameScroll.Opacity = 0.5;
            gameScroll.RenderTransform = Translate(opening ? -3 : 4);

            footer.IsVisible = true;
            footer.Transitions = null;
            footer.Opacity = 0.5;
            footer.RenderTransform = Translate(opening ? -3 : 4);

            _window.FindControl<ScrollViewer>("InstallerScroll")!.IsVisible = true;
            HeadlessUi.Pump(40);
        }

        public void SetReducedEndpoint(bool opening)
        {
            var card = _window.FindControl<Button>("TitanfallCard")!;
            var back = _window.FindControl<Button>("BackToGamesButton")!;

            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            HeadlessUi.Pump(250);
            if (!opening)
            {
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                HeadlessUi.Pump(250);
            }
        }

        public void AssertSnapshot(string name)
        {
            var frame = _window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(new PixelSize(WindowWidth, WindowHeight), frame!.PixelSize);
            SnapshotAssert.Matches(frame, name);
        }

        public void Dispose()
        {
            _window.Close();
            HeadlessUi.Pump(50);
            Loc.Use("en");
        }

        private void SetContentGroupsVisible()
        {
            foreach (var name in ContentGroups)
            {
                var group = _window.FindControl<Control>(name);
                if (group is null)
                {
                    continue;
                }

                group.Transitions = null;
                group.Opacity = 1;
                group.RenderTransform = TransformOperations.Identity;
            }
        }

        private static TransformOperations Translate(double y) =>
            TransformOperations.Parse(
                $"translate(0px, {y.ToString(CultureInfo.InvariantCulture)}px)");
    }

    private static class SnapshotAssert
    {
        private const string UpdateVariable = "UPDATE_TITANFALL_SNAPSHOTS";

        public static void Matches(WriteableBitmap actual, string name)
        {
            var sourcePath = Path.Combine(SourceDirectory, name);
            if (string.Equals(
                    Environment.GetEnvironmentVariable(UpdateVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Directory.CreateDirectory(SourceDirectory);
                actual.Save(sourcePath, PngBitmapEncoderOptions.Default);
                return;
            }

            if (!File.Exists(sourcePath))
            {
                SaveActual(actual, name);
                Assert.Fail(
                    $"Missing Titanfall transition snapshot '{name}'. " +
                    $"Run with {UpdateVariable}=1 to create it.");
            }

            using var expected = new Bitmap(sourcePath);
            using var actualBitmap = Decode(actual);
            Assert.Equal(expected.PixelSize, actualBitmap.PixelSize);
            Assert.Equal(expected.Format, actualBitmap.Format);

            var expectedPixels = ReadPixels(expected);
            var actualPixels = ReadPixels(actualBitmap);
            var firstDifference = -1;
            var differentPixels = 0;
            var stride = actualBitmap.PixelSize.Width * 4;
            for (var offset = 0; offset < actualPixels.Length; offset += 4)
            {
                var delta = Math.Max(
                    Math.Max(
                        Math.Abs(actualPixels[offset] - expectedPixels[offset]),
                        Math.Abs(actualPixels[offset + 1] - expectedPixels[offset + 1])),
                    Math.Max(
                        Math.Abs(actualPixels[offset + 2] - expectedPixels[offset + 2]),
                        Math.Abs(actualPixels[offset + 3] - expectedPixels[offset + 3])));
                if (delta <= ChannelTolerance)
                {
                    continue;
                }

                differentPixels++;
                if (firstDifference < 0)
                {
                    firstDifference = offset / 4;
                }
            }

            if (differentPixels <= MaxDifferentPixels)
            {
                return;
            }

            SaveActual(actual, name);
            var x = firstDifference % actualBitmap.PixelSize.Width;
            var y = firstDifference / actualBitmap.PixelSize.Width;
            Assert.Fail(
                $"Titanfall transition snapshot '{name}' changed: " +
                $"{differentPixels} pixels differ (first at {x},{y}; stride {stride}). " +
                $"Actual frame written to {ActualPath(name)}.");
        }

        private static Bitmap Decode(WriteableBitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            return new Bitmap(stream);
        }

        private static byte[] ReadPixels(Bitmap bitmap)
        {
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            var stride = checked(width * 4);
            var length = checked(stride * height);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, length, stride);
                var pixels = new byte[length];
                Marshal.Copy(buffer, pixels, 0, length);
                return pixels;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void SaveActual(WriteableBitmap actual, string name)
        {
            var directory = Path.Combine(ProjectRoot, "artifacts", "titanfall-snapshots");
            Directory.CreateDirectory(directory);
            actual.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
        }

        private static string ActualPath(string name) =>
            Path.Combine(ProjectRoot, "artifacts", "titanfall-snapshots", name);

        private static string SourceDirectory =>
            Path.Combine(ProjectRoot, "tests", "CircuitLord.VRModInstaller.App.Tests", "Snapshots");

        private static string ProjectRoot
        {
            get
            {
                for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                     directory is not null;
                     directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "CircuitLord.VRModInstaller.Linux.slnx")))
                    {
                        return directory.FullName;
                    }
                }

                return Directory.GetCurrentDirectory();
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            HeadlessUi.Pump(25);
        }
    }
}
