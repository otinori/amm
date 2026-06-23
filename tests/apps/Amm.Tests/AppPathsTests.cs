using System.Diagnostics;
using Amm.Core;

namespace Amm.Tests;

public class AppPathsTests
{
    [Fact]
    public void ProfilesPath_IsUnderBaseDirectory()
    {
        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, "profiles.amm"),
            AppPaths.ProfilesPath);
    }

    [Fact]
    public void ResourcesPath_IsUnderBaseDirectory()
    {
        Assert.Equal(
            Path.Combine(AppPaths.BaseDirectory, "Resources"),
            AppPaths.ResourcesPath);
    }

    [Fact]
    public void BaseDirectory_MatchesProcessDirectory()
    {
        var processPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(processPath));

        var processDir = Path.GetDirectoryName(processPath!);
        Assert.False(string.IsNullOrWhiteSpace(processDir));
        Assert.Equal(processDir, AppPaths.BaseDirectory);
    }

    [Fact]
    public void AppIconPath_IsUnderResourcesDirectory()
    {
        Assert.Equal(
            Path.Combine(AppPaths.ResourcesPath, "amm.ico"),
            AppPaths.AppIconPath);
    }
}
