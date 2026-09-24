using System.Runtime.InteropServices;

namespace Eduli;

internal static class Platform
{
    internal static string OS => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";
    internal static string AdoptiumOS => OS == "osx" ? "mac" : OS;
    internal static string JavaName => OS == "windows" ? "java.exe" : "java";
    // LWJGL 2 and the supplied MinecraftEdu native libraries target Intel x64.
    internal static bool SupportedArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.X64;
    internal static string DataRoot => Environment.GetEnvironmentVariable("EDULI_HOME") ?? (OS switch {
        "windows" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eduli"),
        "osx" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "eduli"),
        _ => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "eduli")
    });
}
