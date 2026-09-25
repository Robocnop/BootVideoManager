using System.IO.Abstractions.TestingHelpers;
using System.Text;
using BootVideoManager.Core.Steam;

namespace BootVideoManager.Core.Tests.Steam;

public class StartupMovieSettingsTests
{
    private const string Root = "/steam";

    // Shape of a real config.vdf (Windows writes LF line endings and tabs).
    private const string ShuffleOff =
        "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\t\t\"x\"\n\t\t}\n\t}\n" +
        "\t\"Customization\"\n\t{\n\t\t\"StartupMovie\"\n\t\t{\n\t\t\t\"MovieID\"\t\t\"0\"\n\t\t\t\"LocalPath\"\t\t\"\"\n\t\t\t\"Shuffle\"\t\t\"0\"\n\t\t}\n\t}\n" +
        "\t\"UI\"\n\t{\n\t\t\"json\"\t\t\"{\\\"a\\\":\\\"}\\\"}\"\n\t}\n}\n";

    private readonly MockFileSystem _fileSystem = new();

    private string ConfigPath => StartupMovieSettings.ConfigPath(_fileSystem, Root);

    private static string On(string text) => text.Replace("\"Shuffle\"\t\t\"0\"", "\"Shuffle\"\t\t\"1\"", StringComparison.Ordinal);

    private void WriteConfig(string text) => _fileSystem.AddFile(ConfigPath, new MockFileData(text));

    private string ReadConfig() => _fileSystem.File.ReadAllText(ConfigPath);

    [Fact]
    public void IsShuffleEnabled_ReadsTheValue()
    {
        WriteConfig(ShuffleOff);
        Assert.False(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));

        WriteConfig(On(ShuffleOff));
        Assert.True(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));
    }

    [Fact]
    public void IsShuffleEnabled_MissingKeyIsOff_MissingOrBrokenFileIsUnknown()
    {
        Assert.Null(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));

        WriteConfig("\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t}\n}\n");
        Assert.False(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));

        WriteConfig("\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n");
        Assert.Null(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));
    }

    [Fact]
    public void EnableShuffle_ChangesOnlyTheValue_AndKeepsABackup()
    {
        WriteConfig(ShuffleOff);

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        Assert.Equal(On(ShuffleOff), ReadConfig());
        Assert.Equal(ShuffleOff, _fileSystem.File.ReadAllText(ConfigPath + StartupMovieSettings.BackupSuffix));
        Assert.True(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));
    }

    [Fact]
    public void EnableShuffle_WhenAlreadyOn_DoesNotTouchTheFile()
    {
        WriteConfig(On(ShuffleOff));

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        Assert.Equal(On(ShuffleOff), ReadConfig());
        Assert.False(_fileSystem.File.Exists(ConfigPath + StartupMovieSettings.BackupSuffix));
    }

    [Fact]
    public void EnableShuffle_AddsTheMissingKey()
    {
        var withoutShuffle = ShuffleOff.Replace("\t\t\t\"Shuffle\"\t\t\"0\"\n", string.Empty, StringComparison.Ordinal);
        WriteConfig(withoutShuffle);

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        Assert.Equal(
            withoutShuffle.Replace("\"LocalPath\"\t\t\"\"\n", "\"LocalPath\"\t\t\"\"\n\t\t\t\"Shuffle\"\t\t\"1\"\n", StringComparison.Ordinal),
            ReadConfig());
    }

    [Fact]
    public void EnableShuffle_CreatesTheMissingBlocks_WithCrlfLineEndings()
    {
        WriteConfig("\"InstallConfigStore\"\r\n{\r\n\t\"Software\"\r\n\t{\r\n\t}\r\n}\r\n");

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        Assert.Equal(
            "\"InstallConfigStore\"\r\n{\r\n\t\"Software\"\r\n\t{\r\n\t}\r\n" +
            "\t\"Customization\"\r\n\t{\r\n\t\t\"StartupMovie\"\r\n\t\t{\r\n\t\t\t\"Shuffle\"\t\t\"1\"\r\n\t\t}\r\n\t}\r\n}\r\n",
            ReadConfig());
        Assert.True(StartupMovieSettings.IsShuffleEnabled(_fileSystem, Root));
    }

    [Fact]
    public void EnableShuffle_KeysAreCaseInsensitive_AndCommentsAreKept()
    {
        const string Text = "// written by Steam\n\"installconfigstore\"\n{\n\t\"CUSTOMIZATION\" { \"startupmovie\" { \"shuffle\" \"0\" } }\n}\n";
        WriteConfig(Text);

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        Assert.Equal(Text.Replace("\"shuffle\" \"0\"", "\"shuffle\" \"1\"", StringComparison.Ordinal), ReadConfig());
    }

    [Fact]
    public void EnableShuffle_KeepsTheUtf8Bom()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(ShuffleOff)]));

        StartupMovieSettings.EnableShuffle(_fileSystem, Root);

        var written = _fileSystem.File.ReadAllBytes(ConfigPath);
        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(On(ShuffleOff))], written);
    }

    [Theory]
    [InlineData("\"Other\"\n{\n}\n")]
    [InlineData("\"InstallConfigStore\"\n{\n\t\"Customization\"\t\t\"oops\"\n}\n")]
    [InlineData("\"InstallConfigStore\"\n{\n\t\"Customization\"\n\t{\n")]
    public void EnableShuffle_RefusesUnexpectedFiles_WithoutWriting(string text)
    {
        WriteConfig(text);

        Assert.Throws<InvalidDataException>(() => StartupMovieSettings.EnableShuffle(_fileSystem, Root));

        Assert.Equal(text, ReadConfig());
        Assert.False(_fileSystem.File.Exists(ConfigPath + StartupMovieSettings.BackupSuffix));
    }

    [Fact]
    public void EnableShuffle_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => StartupMovieSettings.EnableShuffle(_fileSystem, Root));
    }
}
