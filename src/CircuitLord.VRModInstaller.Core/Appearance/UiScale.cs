namespace CircuitLord.VRModInstaller.Core.Appearance;

/// <summary>
/// The application's own density scale, separate from the operating system's
/// display DPI. Avalonia already converts logical pixels to physical pixels;
/// this value only adjusts how many logical pixels the interface requests.
/// </summary>
public static class UiScale
{
    public const int MinimumPercent = 80;
    public const int MaximumPercent = 150;
    public const int DefaultPercent = 100;
    public const int StepPercent = 5;

    public static int ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultPercent;
        }

        var clamped = Math.Clamp(value, MinimumPercent, MaximumPercent);
        var snapped = Math.Round(clamped / StepPercent, MidpointRounding.AwayFromZero) * StepPercent;
        return (int)Math.Clamp(snapped, MinimumPercent, MaximumPercent);
    }

    public static double Factor(int percent) => ClampPercent(percent) / 100.0;

    public static int Scale(double pixels, int percent) => (int)Math.Round(pixels * Factor(percent));
}
