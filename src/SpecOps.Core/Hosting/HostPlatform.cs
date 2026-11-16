namespace SpecOps.Core.Hosting;

[Flags]
public enum ScriptPlatforms
{
    None = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 4,
    Any = Windows | Linux | MacOS,
}

/// <summary>What the current host can do. Windows-only features light up here and are hidden elsewhere.</summary>
public static class HostPlatform
{
    public static ScriptPlatforms Current { get; } =
        OperatingSystem.IsWindows() ? ScriptPlatforms.Windows
        : OperatingSystem.IsLinux() ? ScriptPlatforms.Linux
        : OperatingSystem.IsMacOS() ? ScriptPlatforms.MacOS
        : ScriptPlatforms.None;

    public static bool Supports(ScriptPlatforms platforms) => (platforms & Current) != 0;

    public static bool SupportsImpersonation => OperatingSystem.IsWindows();
}
