using CircuitLord.VRModInstaller.Core.Navigation;

namespace CircuitLord.VRModInstaller.Core.Tests;

public sealed class GamePageTransitionTests
{
    [Fact]
    public void FullPlan_KeepsMotionWithinTheRequestedBudget()
    {
        var plan = GamePageTransitionPlan.Full;

        Assert.True(plan.AnimateArtwork);
        Assert.True(plan.AnimatePages);
        Assert.True(plan.AnimateContent);
        Assert.InRange(plan.ArtworkDuration.TotalMilliseconds, 300, 350);
        Assert.InRange(plan.PageOutDuration.TotalMilliseconds, 150, 200);
        Assert.InRange(plan.PageInDuration.TotalMilliseconds, 220, 280);
        Assert.InRange(plan.ContentRevealDuration.TotalMilliseconds, 180, 250);
        Assert.InRange(plan.ContentStagger.TotalMilliseconds, 25, 40);
    }

    [Fact]
    public void ContentReveal_DelaysAreStaggeredAndBounded()
    {
        var plan = GamePageTransitionPlan.Full;

        Assert.Equal(plan.ContentStartDelay, plan.RevealDelay(0));
        Assert.True(plan.RevealDelay(1) > plan.RevealDelay(0));
        Assert.True(plan.RevealDelay(GamePageTransitionPlan.ContentGroupCount - 1)
            > plan.RevealDelay(GamePageTransitionPlan.ContentGroupCount - 2));
        Assert.True(plan.ContentCompletion < TimeSpan.FromMilliseconds(650));
    }

    [Fact]
    public void ReducedPlan_DisablesEveryAnimatedPart()
    {
        var plan = GamePageTransitionPlan.For(animationsEnabled: false);

        Assert.False(plan.AnimateArtwork);
        Assert.False(plan.AnimatePages);
        Assert.False(plan.AnimateContent);
        Assert.Equal(TimeSpan.Zero, plan.ArtworkDuration);
        Assert.Equal(TimeSpan.Zero, plan.PageOutDuration);
        Assert.Equal(TimeSpan.Zero, plan.PageInDuration);
        Assert.Equal(TimeSpan.Zero, plan.ContentCompletion);
        Assert.Equal(TimeSpan.Zero, plan.RevealDelay(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(GamePageTransitionPlan.ContentGroupCount)]
    public void RevealDelay_RejectsUnknownGroups(int groupIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GamePageTransitionPlan.Full.RevealDelay(groupIndex));
    }
}
