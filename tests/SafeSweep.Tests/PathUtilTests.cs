using SafeSweep.Core.Apps;
using SafeSweep.Core.Safety;

namespace SafeSweep.Tests;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"\\?\C:\Windows\System32")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:folder")]
    [InlineData(@"C:\Temp\*.tmp")]
    [InlineData(@"C:\Temp\file.txt:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_paths_it_cannot_reason_about(string path)
    {
        Assert.Null(PathUtil.TryNormalize(path, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(@"c:/windows/system32/", @"C:\windows\system32")]
    [InlineData(@"C:\Windows\.\Temp\..\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\\System32\\", @"C:\Windows\System32")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalizes_equivalent_spellings(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.TryNormalize(input, out _), ignoreCase: true);
    }

    [Fact]
    public void Containment_is_segment_aware()
    {
        Assert.True(PathUtil.IsStrictlyUnder(@"C:\Windows\System32", @"C:\Windows"));
        Assert.False(PathUtil.IsStrictlyUnder(@"C:\WindowsApps\x", @"C:\Windows"));
        Assert.False(PathUtil.IsStrictlyUnder(@"C:\Windows", @"C:\Windows"));
        Assert.True(PathUtil.IsUnderOrEqual(@"C:\Windows", @"c:\windows"));
    }

    [Fact]
    public void Depth_counts_segments_below_the_drive()
    {
        Assert.Equal(0, PathUtil.Depth(@"C:\"));
        Assert.Equal(1, PathUtil.Depth(@"C:\Windows"));
        Assert.Equal(2, PathUtil.Depth(@"C:\Windows\Temp"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe --flag", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\App\icon.exe,0", @"C:\App\icon.exe")]
    [InlineData(@"rundll32.exe C:\Tools\helper.dll,Entry", @"C:\Tools\helper.dll")]
    [InlineData(@"\??\C:\Drivers\x.sys", @"C:\Drivers\x.sys")]
    public void Extracts_executable_paths_from_commands(string command, string expected)
    {
        Assert.Equal(expected, CommandLineParser.ExtractPath(command), ignoreCase: true);
    }

    [Theory]
    [InlineData("BIJrKtBL", true)]
    [InlineData("lKuwIjuJ", true)]
    [InlineData("OONeCtNC", true)]
    [InlineData("DuboxYunKernel", false)]
    [InlineData("ProcessHacker", false)]
    [InlineData("FACEIT", false)]
    [InlineData("Spiritx", false)]
    [InlineData("df_launcher", false)]
    [InlineData("IObit", false)]
    [InlineData("WinRAR", false)]
    [InlineData("OpenAI", false)]
    [InlineData("PsySH", false)]
    [InlineData("LGHUBData", false)]
    [InlineData("GitHub", false)]
    [InlineData("iPhoneBackup", false)]
    public void Machine_generated_names_are_recognised(string name, bool expected)
    {
        Assert.Equal(expected, NameMatcher.LooksMachineGenerated(name));
    }

    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe", true)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0", true)]
    [InlineData("cr.sb.cdm3E4D1A088C1F6D498C84F3C86DE73CE49F82A104", false)]
    [InlineData("windows_ie_ac_001", false)]
    [InlineData("ActiveSync", false)]
    public void Only_real_package_family_names_count_as_store_data(string name, bool expected)
    {
        Assert.Equal(expected, NameMatcher.IsPackageFamilyName(name));
    }

    [Fact]
    public void Name_matcher_is_generous_about_installed_names()
    {
        var keys = new[] { NameMatcher.Compact("Discord"), NameMatcher.Compact("Visual Studio Code"), NameMatcher.Compact("JetBrains s.r.o.") };
        var tokens = new HashSet<string>(new[] { "Visual Studio Code", "PyCharm 2024.1" }.SelectMany(NameMatcher.Tokens));

        Assert.True(NameMatcher.Matches("discord", keys, tokens, out _));
        Assert.True(NameMatcher.Matches("Visual Studio", keys, tokens, out _));
        Assert.True(NameMatcher.Matches("PyCharm2021.3", keys, tokens, out _));
        Assert.True(NameMatcher.Matches("JetBrains", keys, tokens, out _));
        Assert.False(NameMatcher.Matches("TotallyUnrelatedTool", keys, tokens, out _));
    }
}
