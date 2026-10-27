using System;
using System.IO;
using System.Text;

namespace STS2DualScreen;

/// <summary>
/// The app's files dir and the managed publish dir under it.
///
/// Assembly.Location is not a usable path on the Android engine (assemblies
/// are loaded from memory), so this tries, in order: the assembly location, HOME (which
/// the launcher's GamePrep sets to the files dir before Mono starts), and the
/// package name from /proc/self/cmdline. Same fallbacks as the compat pack's
/// AppPaths.
/// </summary>
public static class DsPaths
{
    private static string _filesDir;

    public static string FilesDir => _filesDir ??= Resolve();

    public static string PublishDir => Path.Combine(FilesDir, ".godot", "mono", "publish", "arm64");

    private static string Resolve()
    {
        try
        {
            // <files>/.godot/mono/publish/arm64/STS2DualScreen.dll. On the
            // Android engine Location is empty or a bare file name.
            var location = typeof(DsPaths).Assembly.Location;
            var dirName = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            if (!string.IsNullOrEmpty(dirName) && Path.IsPathRooted(dirName))
            {
                var dir = new DirectoryInfo(dirName);
                var files = dir.Parent?.Parent?.Parent?.Parent;
                if (dir.Name == "arm64" && files != null)
                    return files.FullName;
            }
        }
        catch (Exception) { }
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home) && Directory.Exists(Path.Combine(home, ".godot", "mono")))
            return home;
        var package = PackageName();
        foreach (var candidate in new[] { $"/data/user/0/{package}/files", $"/data/data/{package}/files" })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }
        return string.IsNullOrEmpty(home) ? "/data/user/0/dev.sts2ds/files" : home;
    }

    private static string PackageName()
    {
        try
        {
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes("/proc/self/cmdline"));
            var name = raw.Split('\0')[0].Trim();
            var colon = name.IndexOf(':');
            name = colon > 0 ? name.Substring(0, colon) : name;
            if (name.Contains('.'))
                return name;
        }
        catch (Exception) { }
        return "dev.sts2ds";
    }
}
