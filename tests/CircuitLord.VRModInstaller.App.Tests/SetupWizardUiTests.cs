using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitLord.VRModInstaller.Localization;

namespace CircuitLord.VRModInstaller.Tests;

public sealed class SetupWizardUiTests : HeadlessUiTest
{
    [Fact]
    public Task SteamStepShowsDistinctGuidanceWithoutRepeatingItsDescription() => Dispatch(() =>
    {
        try
        {
            Loc.Use("en");
            var view = new SetupWizardView();
            var window = new Window
            {
                Width = 800,
                Height = 740,
                Content = view,
            };
            try
            {
                window.Show();
                view.Open(new InstallerViewModel());
                HeadlessUi.Pump(240);

                var primary = view.FindControl<Button>("WizardPrimaryButton")!;
                var title = view.FindControl<TextBlock>("WizardStepTitle")!;
                var expectedTitle = Loc.Get("wizardSteamTitle");
                for (var step = 0; step < 9 && title.Text != expectedTitle; step++)
                {
                    primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    HeadlessUi.Pump(80);
                }

                Assert.Equal(expectedTitle, title.Text);
                var description = view.FindControl<TextBlock>("WizardDescription")!.Text;
                var statusDetail = view.FindControl<TextBlock>("WizardStatusDetail")!.Text;
                var actionHint = view.FindControl<TextBlock>("WizardActionHint")!;

                Assert.False(string.IsNullOrWhiteSpace(description));
                Assert.False(string.IsNullOrWhiteSpace(statusDetail));
                Assert.NotEqual(description, statusDetail);
                Assert.False(actionHint.IsVisible, "The Steam step must not repeat the description as an action hint.");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Loc.Use("en");
        }
    });
}
