namespace CircuitLord.VRModInstaller.Core.Navigation;

/// <summary>
/// Presentation timings for opening and closing a game page. Keeping the plan
/// separate from the window makes the motion policy testable and lets future
/// game pages share the same deliberately short, eased sequence.
/// </summary>
public sealed record GamePageTransitionPlan(
    bool AnimateArtwork,
    bool AnimatePages,
    bool AnimateContent,
    TimeSpan ArtworkDuration,
    TimeSpan PageOutDuration,
    TimeSpan PageInDuration,
    TimeSpan ContentStartDelay,
    TimeSpan ContentRevealDuration,
    TimeSpan ContentStagger,
    TimeSpan OverlayFadeDuration)
{
    public const int ContentGroupCount = 9;

    public static GamePageTransitionPlan Full { get; } = new(
        AnimateArtwork: true,
        AnimatePages: true,
        AnimateContent: true,
        ArtworkDuration: TimeSpan.FromMilliseconds(340),
        PageOutDuration: TimeSpan.FromMilliseconds(170),
        PageInDuration: TimeSpan.FromMilliseconds(250),
        ContentStartDelay: TimeSpan.FromMilliseconds(90),
        ContentRevealDuration: TimeSpan.FromMilliseconds(220),
        ContentStagger: TimeSpan.FromMilliseconds(30),
        OverlayFadeDuration: TimeSpan.FromMilliseconds(70));

    public static GamePageTransitionPlan Reduced { get; } = new(
        AnimateArtwork: false,
        AnimatePages: false,
        AnimateContent: false,
        ArtworkDuration: TimeSpan.Zero,
        PageOutDuration: TimeSpan.Zero,
        PageInDuration: TimeSpan.Zero,
        ContentStartDelay: TimeSpan.Zero,
        ContentRevealDuration: TimeSpan.Zero,
        ContentStagger: TimeSpan.Zero,
        OverlayFadeDuration: TimeSpan.Zero);

    public static GamePageTransitionPlan For(bool animationsEnabled)
        => animationsEnabled ? Full : Reduced;

    public TimeSpan ContentCompletion =>
        ContentStartDelay
        + TimeSpan.FromTicks(ContentStagger.Ticks * (ContentGroupCount - 1))
        + ContentRevealDuration;

    public TimeSpan RevealDelay(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= ContentGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(groupIndex));
        }

        return ContentStartDelay
            + TimeSpan.FromTicks(ContentStagger.Ticks * groupIndex);
    }
}
