using Amm.Core;

namespace Amm.Tests;

public class DialogPathsTests
{
    private const string MyDocs = @"C:\Users\tester\Documents";

    // 既定の環境変数セット (システム系フォルダ)。テストごとに上書き可能。
    private static Func<string, string?> Env(Dictionary<string, string?>? overrides = null)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = @"C:\Users\tester\AppData\Local\Temp",
            ["TMP"] = @"C:\Users\tester\AppData\Local\Temp",
            ["SystemRoot"] = @"C:\Windows",
            ["ProgramData"] = @"C:\ProgramData",
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["ProgramW6432"] = @"C:\Program Files",
            ["LOCALAPPDATA"] = @"C:\Users\tester\AppData\Local",
        };
        if (overrides is not null)
            foreach (var kv in overrides) map[kv.Key] = kv.Value;
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void ExplicitFile_ReturnsItsFolder_RegardlessOfCwd()
    {
        // 規則 1: amm ファイル明示指定時はそのフォルダ (CWD がシステム系でも優先)。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: @"D:\work\proj\my.amm",
            startupCurrentDirectory: @"C:\Windows\System32",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(@"D:\work\proj", result);
    }

    [Fact]
    public void NoExplicitFile_CwdIsSystemFolder_ReturnsMyDocuments()
    {
        // 規則 2: CWD が %ProgramFiles% と一致 → マイドキュメント。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\Program Files",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(MyDocs, result);
    }

    [Fact]
    public void NoExplicitFile_CwdUnderSystemFolder_ReturnsMyDocuments()
    {
        // 規則 2 (配下): System32 は %SystemRoot% 配下 → マイドキュメント。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\Windows\System32",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(MyDocs, result);
    }

    [Fact]
    public void NoExplicitFile_CwdUnderProgramFiles_ReturnsMyDocuments()
    {
        // インストール先 (C:\Program Files\amm) からの起動を想定。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\Program Files\amm",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(MyDocs, result);
    }

    [Fact]
    public void NoExplicitFile_CwdIsNormalFolder_ReturnsCwd()
    {
        // 規則 3: 通常の作業フォルダ → CWD をそのまま。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"D:\work\proj",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(@"D:\work\proj", result);
    }

    [Fact]
    public void UndefinedEnvVars_AreIgnored()
    {
        // 未定義の環境変数は無視。全て未定義なら CWD は通常フォルダ扱い。
        Func<string, string?> emptyEnv = _ => null;
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\Windows",
            myDocuments: MyDocs,
            getEnvironmentVariable: emptyEnv);

        Assert.Equal(@"C:\Windows", result);
    }

    [Fact]
    public void TrailingSeparator_NormalizedBeforeCompare()
    {
        // 末尾セパレータ有無で一致判定がぶれない。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\ProgramData\",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(MyDocs, result);
    }

    [Fact]
    public void SimilarPrefix_IsNotTreatedAsDescendant()
    {
        // "C:\Program Files Custom" は "C:\Program Files" の配下ではない。
        var result = DialogPaths.ResolveInitialDirectory(
            explicitFilePath: null,
            startupCurrentDirectory: @"C:\Program Files Custom",
            myDocuments: MyDocs,
            getEnvironmentVariable: Env());

        Assert.Equal(@"C:\Program Files Custom", result);
    }

    [Fact]
    public void IsSystemFolder_CaseInsensitive()
    {
        Assert.True(DialogPaths.IsSystemFolder(@"c:\windows\system32", Env()));
    }
}
