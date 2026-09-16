using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Steam;

namespace BootVideoManager.Core.Tests.Steam;

public class SteamLocatorTests
{
    private readonly MockFileSystem _fileSystem = new();

    private sealed class FakeRegistry(params string[] values) : ISteamRegistry
    {
        public IEnumerable<string> GetSteamPathCandidates() => values;
    }

    private void AddSteamFolder(string root) => _fileSystem.AddDirectory(_fileSystem.Path.Combine(root, "config"));

    [Fact]
    public void Windows_NormalizesRegistryPath_AndDeduplicatesDefaultLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics.");
        AddSteamFolder(@"C:\Program Files (x86)\Steam");
        var locator = new SteamLocator(
            _fileSystem,
            new SteamLocatorEnvironment(true, @"C:\Users\me", @"C:\Program Files (x86)"),
            new FakeRegistry("c:/program files (x86)/steam"));

        var installation = Assert.Single(locator.FindInstallations());

        Assert.Equal(SteamInstallKind.Registry, installation.Kind);
        Assert.Equal(@"C:\Program Files (x86)\Steam", installation.RootPath);
        Assert.Equal(@"C:\Program Files (x86)\Steam\config\uioverrides\movies", installation.MoviesDirectory);
    }

    [Fact]
    public void Windows_IgnoresStaleRegistryValues_AndFallsBackToDefaultLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics.");
        AddSteamFolder(@"C:\Program Files (x86)\Steam");
        _fileSystem.AddDirectory(@"D:\NotSteam");
        var locator = new SteamLocator(
            _fileSystem,
            new SteamLocatorEnvironment(true, @"C:\Users\me", @"C:\Program Files (x86)"),
            new FakeRegistry("", "E:/Uninstalled/Steam", @"D:\NotSteam", "\0bad"));

        var installation = Assert.Single(locator.FindInstallations());

        Assert.Equal(SteamInstallKind.DefaultLocation, installation.Kind);
    }

    [Fact]
    public void Windows_ReportsSeveralLibrariesInPriorityOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path semantics.");
        AddSteamFolder(@"D:\Games\Steam");
        AddSteamFolder(@"C:\Program Files (x86)\Steam");
        var locator = new SteamLocator(
            _fileSystem,
            new SteamLocatorEnvironment(true, @"C:\Users\me", @"C:\Program Files (x86)"),
            new FakeRegistry("D:/Games/Steam"));

        Assert.Equal(
            [SteamInstallKind.Registry, SteamInstallKind.DefaultLocation],
            locator.FindInstallations().Select(i => i.Kind));
    }

    [Fact]
    public void Linux_FindsNativeFlatpakAndSnapInstalls()
    {
        var home = MockUnixSupport.Path(@"C:\home\deck");
        AddSteamFolder(_fileSystem.Path.Combine(home, ".local", "share", "Steam"));
        AddSteamFolder(_fileSystem.Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        AddSteamFolder(_fileSystem.Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        var locator = new SteamLocator(_fileSystem, new SteamLocatorEnvironment(false, home, null), registry: null);

        var installations = locator.FindInstallations();

        Assert.Equal(
            [SteamInstallKind.Native, SteamInstallKind.Flatpak, SteamInstallKind.Snap],
            installations.Select(i => i.Kind));
        Assert.EndsWith(
            _fileSystem.Path.Combine("Steam", "config", "uioverrides", "movies"),
            installations[0].MoviesDirectory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_NoSteam_FindsNothing()
    {
        var locator = new SteamLocator(_fileSystem, new SteamLocatorEnvironment(false, MockUnixSupport.Path(@"C:\home\deck"), null), null);

        Assert.Empty(locator.FindInstallations());
    }

    [Fact]
    public void Manual_AcceptsOnlyFoldersThatLookLikeSteam()
    {
        var steam = MockUnixSupport.Path(@"C:\Games\SteamLibrary");
        var random = MockUnixSupport.Path(@"C:\Users\me\Documents");
        _fileSystem.AddFile(_fileSystem.Path.Combine(steam, "steam.sh"), new MockFileData("#!/bin/sh"));
        _fileSystem.AddDirectory(random);
        var locator = new SteamLocator(_fileSystem, new SteamLocatorEnvironment(false, random, null), null);

        Assert.Equal(SteamInstallKind.Manual, locator.TryCreateManual(steam)?.Kind);
        Assert.Null(locator.TryCreateManual(random));
        Assert.Null(locator.TryCreateManual(MockUnixSupport.Path(@"C:\does\not\exist")));
        Assert.Null(locator.TryCreateManual("   "));
    }
}
