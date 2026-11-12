using System.Runtime.Versioning;

// The installer is a Linux desktop application by definition: XDG paths,
// desktop entries, Steam's shortcuts.vdf and the tf2vr launcher are all Linux
// specific. Declaring it here keeps platform analyzers honest instead of
// suppressing their warnings.
[assembly: SupportedOSPlatform("linux")]
