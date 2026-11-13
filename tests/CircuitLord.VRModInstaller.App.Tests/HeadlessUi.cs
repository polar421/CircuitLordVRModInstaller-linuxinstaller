using System;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Threading;

namespace CircuitLord.VRModInstaller.Tests;

internal static class HeadlessUi
{
    public static void Pump(int milliseconds = 100)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Thread.Sleep(15);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
