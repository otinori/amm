using System.Text;
using System.Text.Json;
using Amm.Core;

namespace Amm.Tests;

public class SessionProfileTests
{
    [Fact]
    public void CommandLine_NoArgs_ReturnsExecutable()
    {
        var p = new SessionProfile { Executable = "cmd.exe" };
        Assert.Equal("cmd.exe", p.CommandLine);
    }

    [Fact]
    public void CommandLine_WithArgs_JoinsWithSpace()
    {
        var p = new SessionProfile
        {
            Executable = "powershell.exe",
            Args = ["-NoLogo", "-NoProfile"],
        };
        Assert.Equal("powershell.exe -NoLogo -NoProfile", p.CommandLine);
    }

    [Fact]
    public void CommandLine_ExpandsEnvVarsInExecutableAndArgs()
    {
        var p = new SessionProfile
        {
            Executable = "cmd.exe",
            Args = ["/c", "%APPDATA%\\npm\\copilot.cmd"],
        };
        var cmd = p.CommandLine;
        Assert.DoesNotContain("%APPDATA%", cmd);
        var expectedAppData = Environment.GetEnvironmentVariable("APPDATA");
        Assert.NotNull(expectedAppData);
        Assert.Contains(expectedAppData, cmd);
    }

    [Fact]
    public void ResolveExecutablePath_BareName_ResolvesToAbsoluteOnPath()
    {
        // cmd.exe は Windows のどの環境でも System32 に存在する。
        var p = new SessionProfile { Executable = "cmd.exe" };
        var resolved = p.ResolveExecutablePath();
        Assert.True(Path.IsPathRooted(resolved), $"expected rooted path, got: {resolved}");
        Assert.EndsWith("cmd.exe", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void ResolveExecutablePath_UnknownBareName_FallsBackToOriginal()
    {
        // PATH に存在しない裸名は元のまま (従来動作を壊さないフォールバック)。
        var p = new SessionProfile { Executable = "definitely-not-a-real-exe-xyz123.exe" };
        Assert.Equal("definitely-not-a-real-exe-xyz123.exe", p.ResolveExecutablePath());
    }

    [Fact]
    public void ResolveExecutablePath_ExplicitPath_NotSearched()
    {
        // パス区切りを含む明示パスは PATH 探索せずそのまま返す。
        var p = new SessionProfile { Executable = @"C:\custom\dir\tool.exe" };
        Assert.Equal(@"C:\custom\dir\tool.exe", p.ResolveExecutablePath());
    }

    [Fact]
    public void BuildLaunchCommandLine_BareName_ResolvedAndUnquotedIfNoSpace()
    {
        var p = new SessionProfile { Executable = "cmd.exe", Args = ["/c", "echo", "hi"] };
        var cmd = p.BuildLaunchCommandLine();
        Assert.True(Path.IsPathRooted(cmd.Split(' ')[0]) || cmd.StartsWith("\""));
        Assert.EndsWith("/c echo hi", cmd);
    }

    [Fact]
    public void BuildLaunchCommandLine_ExeWithSpaces_IsQuoted()
    {
        var p = new SessionProfile { Executable = @"C:\Program Files\amm\claude.exe", Args = ["--foo"] };
        var cmd = p.BuildLaunchCommandLine();
        Assert.StartsWith("\"C:\\Program Files\\amm\\claude.exe\"", cmd);
        Assert.EndsWith("--foo", cmd);
    }

    [Fact]
    public void BuildLaunchCommandLine_ExeWithoutSpaces_NotQuoted()
    {
        var p = new SessionProfile { Executable = @"C:\tools\claude.exe", Args = [] };
        Assert.Equal(@"C:\tools\claude.exe", p.BuildLaunchCommandLine());
    }

    [Fact]
    public void CommandLine_DisplayForm_UnchangedByResolution()
    {
        // 表示用 CommandLine は従来どおり解決・クォートしない (後方互換)。
        var p = new SessionProfile { Executable = "cmd.exe", Args = ["/c", "x"] };
        Assert.Equal("cmd.exe /c x", p.CommandLine);
    }

    [Fact]
    public void GetEncoding_Utf8_ReturnsUtf8()
    {
        var p = new SessionProfile { OutputEncoding = "UTF-8" };
        Assert.Equal(Encoding.UTF8, p.GetEncoding());
    }

    [Fact]
    public void GetEncoding_ShiftJis_Returns932()
    {
        var p = new SessionProfile { OutputEncoding = "Shift_JIS" };
        Assert.Equal(932, p.GetEncoding().CodePage);
    }

    [Fact]
    public void GetEncoding_Unknown_FallsBackToUtf8()
    {
        var p = new SessionProfile { OutputEncoding = "EBCDIC" };
        Assert.Equal(Encoding.UTF8, p.GetEncoding());
    }

    [Fact]
    public void ResolveWorkingDirectory_Null_ReturnsCurrentDirectory()
    {
        var p = new SessionProfile();
        Assert.Equal(Environment.CurrentDirectory, p.ResolveWorkingDirectory());
    }

    [Fact]
    public void ResolveWorkingDirectory_Whitespace_ReturnsCurrentDirectory()
    {
        var p = new SessionProfile { WorkingDirectory = "   " };
        Assert.Equal(Environment.CurrentDirectory, p.ResolveWorkingDirectory());
    }

    [Fact]
    public void ResolveWorkingDirectory_LiteralPath_ReturnsSame()
    {
        var p = new SessionProfile { WorkingDirectory = @"C:\temp" };
        Assert.Equal(@"C:\temp", p.ResolveWorkingDirectory());
    }

    [Fact]
    public void ResolveWorkingDirectory_EnvVar_Expanded()
    {
        var p = new SessionProfile { WorkingDirectory = "%USERPROFILE%" };
        var resolved = p.ResolveWorkingDirectory();
        Assert.NotNull(resolved);
        Assert.DoesNotContain("%", resolved);
        Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE"), resolved);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultCmdProfile()
    {
        var profiles = SessionProfileLoader.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Single(profiles);
        Assert.Equal("CMD", profiles[0].Name);
    }

    [Fact]
    public void Load_RealJson_DeserializesAllFields()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                {
                  "name": "Claude Code",
                  "executable": "claude.exe",
                  "args": [],
                  "newlineMode": "LF",
                  "outputEncoding": "UTF-8",
                  "autoChcp": false,
                  "waitPatterns": [">\\s*$"],
                  "workingDirectory": "%USERPROFILE%"
                }
              ]
            }
            """);

            var profiles = SessionProfileLoader.Load(tmp);

            Assert.Single(profiles);
            var p = profiles[0];
            Assert.Equal("Claude Code", p.Name);
            Assert.Equal(NewlineMode.LF, p.NewlineMode);
            Assert.False(p.AutoChcp);
            Assert.Single(p.WaitPatterns);
            Assert.Equal("%USERPROFILE%", p.WorkingDirectory);
            Assert.Equal(Environment.GetEnvironmentVariable("USERPROFILE"), p.ResolveWorkingDirectory());
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Load_InvalidJson_ThrowsInvalidDataException()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                {
                  "name": "Broken",
                  "workingDirectory": "C:\Invalid\Path"
                }
              ]
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => SessionProfileLoader.Load(tmp));
            Assert.Contains("profiles", ex.Message);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---- v2: per-command 設定 (UDR-amm-20260427T0055-2c1) ----

    [Fact]
    public void V2Defaults_MatchSpecifiedFallbacks()
    {
        // キー欠落時の既定値が現挙動互換であることを確認 (verification criteria)
        var p = new SessionProfile();
        Assert.Equal(0, p.AutoStartCount);
        Assert.False(p.CloseProhibited);
        Assert.True(p.CollapseBlankLines);
        // "#" は Markdown 見出しと衝突するため既定から除外 (旧既定は ["'", "//", "#"])
        Assert.Equal(new[] { "'", "//" }, p.CommentPrefixes);
        Assert.Empty(p.WindowGeometry);
        Assert.False(p.SelectWorkingDirOnStart);
        Assert.False(p.PromptNewNameOnCommandAdd);
        Assert.Empty(p.QuickPrompts);
    }

    [Fact]
    public void Load_V1Json_AppliesV2Defaults()
    {
        // 旧 profiles.amm に v2 キーが無くても既定値で動作する後方互換性
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                { "name": "CMD", "executable": "cmd.exe" }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            var p = profiles[0];
            Assert.Equal(0, p.AutoStartCount);
            Assert.False(p.CloseProhibited);
            Assert.True(p.CollapseBlankLines);
            Assert.Equal(new[] { "'", "//" }, p.CommentPrefixes);
            Assert.Empty(p.WindowGeometry);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Load_V2Json_DeserializesAllNewFields()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                {
                  "name": "Claude",
                  "executable": "claude.exe",
                  "autoStartCount": 2,
                  "closeProhibited": true,
                  "collapseBlankLines": false,
                  "commentPrefixes": ["#"],
                  "windowGeometry": [
                    { "index": 1, "x": 0,   "y": 0, "w": 640, "h": 480 },
                    { "index": 2, "x": 640, "y": 0, "w": 640, "h": 480 }
                  ]
                }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            var p = profiles[0];
            Assert.Equal(2, p.AutoStartCount);
            Assert.True(p.CloseProhibited);
            Assert.False(p.CollapseBlankLines);
            Assert.Equal(new[] { "#" }, p.CommentPrefixes);
            Assert.Equal(2, p.WindowGeometry.Length);
            Assert.Equal(640, p.WindowGeometry[1].X);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Load_LegacyDefaultCommentPrefixes_MigratesHashOut()
    {
        // 旧既定 ["'", "//", "#"] のまま保存された AMM ファイルは "#" 抜きへ
        // 透過移行する (Markdown 見出し ## が送信から抜け落ちる問題の修正)。
        // 意図して "#" を使う構成 (例: ["#"]) は Load_V2Json_DeserializesAllNewFields
        // が保全を確認している。
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                { "name": "Claude", "executable": "claude.exe", "commentPrefixes": ["'", "//", "#"] }
              ]
            }
            """);
            var p = SessionProfileLoader.Load(tmp)[0];
            Assert.Equal(new[] { "'", "//" }, p.CommentPrefixes);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void FilterLinesForSend_DefaultPrefixes_KeepMarkdownHeadings()
    {
        // 新既定では Markdown 見出し行 (#, ##, ...) が送信から抜けない
        var p = new SessionProfile();
        var result = p.FilterLinesForSend(["# 見出し", "## 小見出し", "本文", "// コメント"]);
        Assert.Equal(new[] { "# 見出し", "## 小見出し", "本文" }, result);
    }

    [Fact]
    public void Load_V2Json_DeserializesPromptRenameAndQuickPrompts()
    {
        // 本日追加 (UDR-amm-20260521T1125-757, UDR-amm-20260521T1144-d6f) の
        // per-command 設定が JSON から正しく往復することを確認。
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                {
                  "name": "Claude",
                  "executable": "claude.exe",
                  "selectWorkingDirOnStart": true,
                  "promptNewNameOnCommandAdd": true,
                  "quickPrompts": [
                    { "label": "OK",   "prompt": "OK" },
                    { "label": "続行", "prompt": "続行してください" }
                  ]
                }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            var p = profiles[0];
            Assert.True(p.SelectWorkingDirOnStart);
            Assert.True(p.PromptNewNameOnCommandAdd);
            Assert.Equal(2, p.QuickPrompts.Length);
            Assert.Equal("OK", p.QuickPrompts[0].Label);
            Assert.Equal("OK", p.QuickPrompts[0].Prompt);
            Assert.Equal("続行", p.QuickPrompts[1].Label);
            Assert.Equal("続行してください", p.QuickPrompts[1].Prompt);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TryGetGeometryForIndex_HitAndMiss()
    {
        var p = new SessionProfile
        {
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, X = 10, Y = 20, W = 640, H = 480 },
                new WindowGeometryEntry { Index = 3, X = 30, Y = 40, W = 320, H = 240 },
            ],
        };
        Assert.True(p.TryGetGeometryForIndex(1, out var r1));
        Assert.Equal(new Rectangle(10, 20, 640, 480), r1);

        // index 2 は穴 (定義なし) → false
        Assert.False(p.TryGetGeometryForIndex(2, out _));

        Assert.True(p.TryGetGeometryForIndex(3, out var r3));
        Assert.Equal(new Rectangle(30, 40, 320, 240), r3);
    }

    [Fact]
    public void TryGetGeometryForIndex_NameOnlyEntry_ReturnsFalse()
    {
        // W=H=0 のエントリは name-only / maximized なので geometry 取得失敗扱い
        var p = new SessionProfile
        {
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, Name = "main", Maximized = true },
                new WindowGeometryEntry { Index = 2, X = 0, Y = 0, W = 0, H = 0, Name = "side" },
            ],
        };
        Assert.False(p.TryGetGeometryForIndex(1, out _));
        Assert.False(p.TryGetGeometryForIndex(2, out _));
    }

    [Fact]
    public void TryGetNameForIndex_HitMissAndNameOnly()
    {
        var p = new SessionProfile
        {
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, X = 10, Y = 20, W = 640, H = 480, Name = "main" },
                new WindowGeometryEntry { Index = 2, X = 30, Y = 40, W = 320, H = 240 }, // 名前なし
                new WindowGeometryEntry { Index = 3, Name = "named-max", Maximized = true }, // name-only + max
            ],
        };
        Assert.True(p.TryGetNameForIndex(1, out var n1));
        Assert.Equal("main", n1);

        Assert.False(p.TryGetNameForIndex(2, out _));

        Assert.True(p.TryGetNameForIndex(3, out var n3));
        Assert.Equal("named-max", n3);

        // 存在しない index
        Assert.False(p.TryGetNameForIndex(99, out _));
    }

    [Fact]
    public void WindowGeometry_JsonRoundTrip_PreservesNameAndMaximized()
    {
        var original = new SessionProfile
        {
            Name = "Claude",
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, X = 0, Y = 0, W = 640, H = 480, Name = "main" },
                new WindowGeometryEntry { Index = 2, Name = "side", Maximized = true },
            ],
        };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<SessionProfile>(json)!;

        Assert.Equal(2, loaded.WindowGeometry.Length);
        Assert.Equal("main", loaded.WindowGeometry[0].Name);
        Assert.Null(loaded.WindowGeometry[0].Maximized); // null/false 未設定
        Assert.Equal("side", loaded.WindowGeometry[1].Name);
        Assert.True(loaded.WindowGeometry[1].Maximized);
    }

    [Fact]
    public void WindowGeometry_JsonBackwardCompatibility_OldFileWithoutName()
    {
        // 旧 AMM ファイル (name / maximized フィールド無し) でも読める
        var json = """
        {
          "windowGeometry": [
            { "index": 1, "x": 0, "y": 0, "w": 800, "h": 600 }
          ]
        }
        """;
        var p = JsonSerializer.Deserialize<SessionProfile>(json)!;
        Assert.Single(p.WindowGeometry);
        Assert.Null(p.WindowGeometry[0].Name);
        Assert.Null(p.WindowGeometry[0].Maximized);
    }

    [Fact]
    public void WindowGeometry_JsonRoundTrip_PreservesWorkingDirectory()
    {
        var original = new SessionProfile
        {
            Name = "Claude",
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, X = 0, Y = 0, W = 800, H = 600, WorkingDirectory = "C:\\proj\\a" },
                new WindowGeometryEntry { Index = 2, Maximized = true, WorkingDirectory = "C:\\proj\\b", Name = "b" },
                new WindowGeometryEntry { Index = 3, X = 10, Y = 20, W = 320, H = 240 }, // cwd なし
            ],
        };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<SessionProfile>(json)!;

        Assert.Equal(3, loaded.WindowGeometry.Length);
        Assert.Equal("C:\\proj\\a", loaded.WindowGeometry[0].WorkingDirectory);
        Assert.Equal("C:\\proj\\b", loaded.WindowGeometry[1].WorkingDirectory);
        Assert.True(loaded.WindowGeometry[1].Maximized);
        Assert.Null(loaded.WindowGeometry[2].WorkingDirectory);
    }

    [Fact]
    public void TryGetWorkingDirectoryForIndex_HitMissAndEmpty()
    {
        var p = new SessionProfile
        {
            WindowGeometry =
            [
                new WindowGeometryEntry { Index = 1, X = 0, Y = 0, W = 800, H = 600, WorkingDirectory = "C:\\proj" },
                new WindowGeometryEntry { Index = 2, X = 10, Y = 20, W = 320, H = 240 }, // 未設定
                new WindowGeometryEntry { Index = 3, X = 30, Y = 40, W = 320, H = 240, WorkingDirectory = "   " }, // 空白のみ
                new WindowGeometryEntry { Index = 4, X = 50, Y = 60, W = 320, H = 240, WorkingDirectory = "" }, // 空文字
            ],
        };
        Assert.True(p.TryGetWorkingDirectoryForIndex(1, out var cwd1));
        Assert.Equal("C:\\proj", cwd1);

        Assert.False(p.TryGetWorkingDirectoryForIndex(2, out var cwd2));
        Assert.Equal("", cwd2);

        Assert.False(p.TryGetWorkingDirectoryForIndex(3, out _)); // whitespace は未設定扱い
        Assert.False(p.TryGetWorkingDirectoryForIndex(4, out _)); // 空文字は未設定扱い

        // 存在しない index
        Assert.False(p.TryGetWorkingDirectoryForIndex(99, out _));
    }

    [Fact]
    public void LegacyPromptRenameOnStart_MigratesToPromptNewNameOnCommandAdd()
    {
        // 旧フィールド名 promptRenameOnStart は Load 時に新フィールドへ移行され、
        // 以後の Serialize でも書き戻されない。
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                {
                  "name": "Legacy",
                  "executable": "cmd.exe",
                  "promptRenameOnStart": true
                }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            var p = profiles[0];
            Assert.True(p.PromptNewNameOnCommandAdd);
            // 旧キーは ExtraProperties から除去されているはず
            Assert.True(p.ExtraProperties == null || !p.ExtraProperties.ContainsKey("promptRenameOnStart"));

            // 再 Serialize で旧キーが書き戻されないこと
            var rehydrated = JsonSerializer.Serialize(p);
            Assert.DoesNotContain("promptRenameOnStart", rehydrated);
            Assert.Contains("promptNewNameOnCommandAdd", rehydrated);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void IsCommentLine_DefaultPrefixes()
    {
        var p = new SessionProfile();
        Assert.True(p.IsCommentLine("' VBS comment"));
        Assert.True(p.IsCommentLine("// JS comment"));
        // "#" は Markdown 見出しと衝突するため既定から除外 (per-command 設定で追加は可能)
        Assert.False(p.IsCommentLine("# Markdown 見出し"));
        Assert.False(p.IsCommentLine("regular line"));
        Assert.False(p.IsCommentLine("  // インデント付きは対象外"));
    }

    [Fact]
    public void IsCommentLine_EmptyPrefixes_NeverMatches()
    {
        var p = new SessionProfile { CommentPrefixes = [] };
        Assert.False(p.IsCommentLine("// still not a comment"));
        Assert.False(p.IsCommentLine("' nope"));
    }

    [Fact]
    public void IsCommentLine_CustomPrefixes()
    {
        var p = new SessionProfile { CommentPrefixes = ["#", ";"] };
        Assert.True(p.IsCommentLine("# bash"));
        Assert.True(p.IsCommentLine("; ini"));
        Assert.False(p.IsCommentLine("// no longer matched"));
    }

    [Fact]
    public void FilterLinesForSend_DefaultsCollapseBlanksAndDropComments()
    {
        var p = new SessionProfile();
        var lines = new[]
        {
            "first",
            "",
            "",
            "// dropped",
            "second",
            "' also dropped",
            "",
            "third",
        };
        var result = p.FilterLinesForSend(lines);
        // 連続空行は 1 行に縮約、コメント行は除去
        Assert.Equal(new[] { "first", "", "second", "", "third" }, result);
    }

    [Fact]
    public void FilterLinesForSend_CollapseDisabled_KeepsAllBlanks()
    {
        var p = new SessionProfile { CollapseBlankLines = false };
        var lines = new[] { "a", "", "", "", "b" };
        var result = p.FilterLinesForSend(lines);
        Assert.Equal(new[] { "a", "", "", "", "b" }, result);
    }

    [Fact]
    public void FilterLinesForSend_NoCommentPrefixes_KeepsAllNonBlank()
    {
        var p = new SessionProfile { CommentPrefixes = [] };
        var lines = new[] { "// preserved", "// preserved 2" };
        var result = p.FilterLinesForSend(lines);
        Assert.Equal(2, result.Count);
    }

    // ---- Phase 3: MCP nickname (UDR-amm-20260427T0225-7a3) ----

    [Fact]
    public void Nickname_DefaultIsNull()
    {
        var p = new SessionProfile();
        Assert.Null(p.Nickname);
    }

    [Fact]
    public void Load_WithNickname_DeserializesField()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                { "name": "Claude", "executable": "claude.exe", "nickname": "claude" }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            Assert.Equal("claude", profiles[0].Nickname);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Load_WithoutNickname_LeavesNull()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, """
            {
              "profiles": [
                { "name": "Plain", "executable": "cmd.exe" }
              ]
            }
            """);
            var profiles = SessionProfileLoader.Load(tmp);
            Assert.Null(profiles[0].Nickname);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ---- regression: "未保存の変更" ダイアログ誤検出 (autoChcp=false 等の roundtrip 欠落) ----
    // MdiParentForm の baseline は JSON roundtrip ではなく JsonClone (デフォルト
    // オプション = WhenWritingDefault を使わない) で snapshot を取り、その snapshot
    // との差分を出すことで、初期化子と型デフォルトが異なる bool プロパティの
    // 値消失を回避する。本テストは snapshot 経路がロスレスであることを保証する。
    [Fact]
    public void Snapshot_LosslessRoundtrip_PreservesNonDefaultBoolValues()
    {
        // テストアセンブリ (tests/apps/Amm.Tests/bin/Debug/net9.0-windows) から
        // リポジトリ直下まで 6 階層上がり、src/apps/Amm/profiles.amm を指す。
        var ammDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "apps", "Amm"));
        var profilesPath = Path.Combine(ammDir, "profiles.amm");
        Assert.True(File.Exists(profilesPath), $"profiles.amm not found at {profilesPath}");

        var loaded = SessionProfileLoader.Load(profilesPath);

        // MdiParentForm.SnapshotProfiles と同じ手順 (JsonClone = default options)
        static SessionProfile Clone(SessionProfile p)
        {
            var json = JsonSerializer.Serialize(p);
            return JsonSerializer.Deserialize<SessionProfile>(json)!;
        }
        var baseline = loaded.Select(Clone).ToArray();

        Assert.Equal(loaded.Length, baseline.Length);

        var mismatches = new List<string>();
        for (int i = 0; i < loaded.Length; i++)
        {
            var bJson = JsonSerializer.Serialize(baseline[i]);
            var cJson = JsonSerializer.Serialize(loaded[i]);
            if (!string.Equals(bJson, cJson, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"--- Profile[{i}] {loaded[i].Name} mismatch ---\n" +
                    $"baseline: {bJson}\n" +
                    $"loaded  : {cJson}\n");
            }
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail("Unchanged profiles flagged as different:\n" +
                string.Join("\n", mismatches));
        }
    }

    // ---- EscapeNickname (MCP 安全化) ----

    [Theory]
    [InlineData("My Task", "My_Task")]      // 空白 → _
    [InlineData("a#b", "a_b")]               // '#' (participant Key 区切り) → _
    [InlineData("  lead trail  ", "lead_trail")] // 前後空白除去 + 内部空白 → _
    [InlineData("a   b", "a_b")]             // 連続空白 → 単一 _
    [InlineData("claude", "claude")]         // そのまま
    [InlineData("日本語名", "日本語名")]       // Unicode は保持
    public void EscapeNickname_NormalizesUnsafeChars(string raw, string expected)
        => Assert.Equal(expected, SessionProfile.EscapeNickname(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("###")]   // 全部不正 → 結果空 → null
    public void EscapeNickname_EmptyOrAllUnsafe_ReturnsNull(string raw)
        => Assert.Null(SessionProfile.EscapeNickname(raw));

    // ---- ResumeArgsFor / EffectiveArgs (CommandType 起点) ----

    [Theory]
    [InlineData(CommandType.ClaudeCode, "--resume")]
    [InlineData(CommandType.CopilotCli, "--resume")]
    [InlineData(CommandType.Codex, "resume")]
    public void ResumeArgsFor_ByCommandType(CommandType type, string expected)
    {
        var p = new SessionProfile { CommandType = type };
        Assert.Equal([expected], SessionProfile.ResumeArgsFor(p));
    }

    [Theory]
    [InlineData(CommandType.Cmd)]
    [InlineData(CommandType.PowerShell)]
    [InlineData(CommandType.Other)]
    public void ResumeArgsFor_NonResumable_Empty(CommandType type)
        => Assert.Empty(SessionProfile.ResumeArgsFor(new SessionProfile { CommandType = type }));

    [Fact]
    public void EffectiveArgs_ResumeOff_ReturnsArgsUnchanged()
    {
        var p = new SessionProfile { CommandType = CommandType.ClaudeCode, Args = ["x"], ResumeOnStart = false };
        Assert.Equal(["x"], p.EffectiveArgs());
    }

    [Fact]
    public void EffectiveArgs_ResumeOn_AppendsTokenAtEnd()
    {
        // codex の "resume" サブコマンドが引数末尾に付くことを確認 (cmd /c codex.cmd resume)。
        var p = new SessionProfile
        {
            CommandType = CommandType.Codex,
            Args = ["/c", "%APPDATA%\\npm\\codex.cmd"],
            ResumeOnStart = true,
        };
        Assert.Equal(["/c", "%APPDATA%\\npm\\codex.cmd", "resume"], p.EffectiveArgs());
    }

    // ---- PresetFor / CommandType 推測 ----

    [Fact]
    public void PresetFor_KnownType_ReturnsMatchingTemplate()
    {
        var preset = SessionProfile.PresetFor(CommandType.ClaudeCode);
        Assert.NotNull(preset);
        Assert.Equal(CommandType.ClaudeCode, preset!.CommandType);
        Assert.Equal("claude.exe", preset.Executable);
    }

    [Fact]
    public void PresetFor_Other_ReturnsNull()
        => Assert.Null(SessionProfile.PresetFor(CommandType.Other));

    [Theory]
    [InlineData("claude", "claude.exe", CommandType.ClaudeCode)]   // nickname 優先
    [InlineData(null, "powershell.exe", CommandType.PowerShell)]   // executable から
    [InlineData(null, "cmd.exe", CommandType.Cmd)]
    public void MigrateLegacyFields_InfersCommandType_WhenAbsent(string? nickname, string exe, CommandType expected)
    {
        var p = new SessionProfile { Nickname = nickname, Executable = exe }; // commandType 未指定 = Other
        p.MigrateLegacyFields();
        Assert.Equal(expected, p.CommandType);
    }

    [Fact]
    public void MigrateLegacyFields_InfersCodexFromArgs()
    {
        var p = new SessionProfile { Executable = "cmd.exe", Args = ["/c", "%APPDATA%\\npm\\codex.cmd"] };
        p.MigrateLegacyFields();
        Assert.Equal(CommandType.Codex, p.CommandType);
    }
}
