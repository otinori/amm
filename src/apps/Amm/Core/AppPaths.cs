using System.Diagnostics;

namespace Amm.Core;

public static class AppPaths
{
    private static readonly string[] ProfileFileNames = ["profiles.amm"];

    public static string BaseDirectory
    {
        get
        {
            var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                var processDir = Path.GetDirectoryName(mainModulePath);
                if (!string.IsNullOrWhiteSpace(processDir))
                    return processDir;
            }

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var processDir = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(processDir))
                    return processDir;
            }

            return AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    public static string ProfilesPath => FindProfilesPath();
    public static string ResourcesPath => Path.Combine(BaseDirectory, "Resources");
    public static string AppIconPath => Path.Combine(ResourcesPath, "amm.ico");

    public static string FindProfilesPath()
    {
        foreach (var fileName in ProfileFileNames)
        {
            var candidate = Path.Combine(BaseDirectory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(BaseDirectory, ProfileFileNames[0]);
    }
}
