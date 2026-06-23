using Amm.Core;

namespace Amm.Tests;

public class AppLaunchOptionsTests
{
    [Fact]
    public void Parse_NoArgs_UsesDefaultProfilesPath()
    {
        var options = AppLaunchOptions.Parse([]);
        Assert.Equal(AppPaths.ProfilesPath, options.ProfilesPath);
        Assert.False(options.AutoStartAll);
        Assert.False(options.HasExplicitFile);
    }

    [Fact]
    public void Parse_ProfilesPath_ResolvesToAbsolutePath()
    {
        var relativePath = @".\custom-profiles.json";
        var expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relativePath));

        var options = AppLaunchOptions.Parse([relativePath]);

        Assert.Equal(expected, options.ProfilesPath);
        Assert.True(options.HasExplicitFile);
    }

    [Fact]
    public void Parse_StartAll_SetsFlag()
    {
        var options = AppLaunchOptions.Parse(["--start-all"]);
        Assert.True(options.AutoStartAll);
    }

    [Fact]
    public void Parse_ProfilesPathAndStartAll_BothWork()
    {
        var relativePath = @".\custom-profiles.json";
        var expected = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relativePath));

        var options = AppLaunchOptions.Parse([relativePath, "--start-all"]);

        Assert.Equal(expected, options.ProfilesPath);
        Assert.True(options.AutoStartAll);
    }

    [Fact]
    public void Parse_MultipleProfilesPaths_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["a.json", "b.json"]));
        Assert.Contains("1 つだけ", ex.Message);
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => AppLaunchOptions.Parse(["--nope"]));
        Assert.Contains("--nope", ex.Message);
    }
}
