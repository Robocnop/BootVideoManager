using BootVideoManager.Core.Install;

namespace BootVideoManager.Core.Tests.Install;

public class VideoFileNamesTests
{
    [Theory]
    [InlineData("star_wars_intro_disney", "MnZgE", "star_wars_intro_disney_MnZgE.webm")]
    [InlineData("Star Wars: Episode IV — Pokémon!", "MnZgE", "star_wars_episode_iv_pokemon_MnZgE.webm")]
    [InlineData("half-life-2", "Ab1Cd", "half-life-2_Ab1Cd.webm")]
    [InlineData("!!!", "Ab1Cd", "video_Ab1Cd.webm")]
    [InlineData("日本語", "Ab1Cd", "video_Ab1Cd.webm")]
    [InlineData("../../etc/passwd", "Ab1Cd", "etc_passwd_Ab1Cd.webm")]
    public void ForPost_ProducesPortableUniqueNames(string slug, string id, string expected)
    {
        var post = PostFactory.Create(id) with { Slug = slug };

        Assert.Equal(expected, VideoFileNames.ForPost(post));
    }

    [Fact]
    public void ForPost_TruncatesVeryLongSlugs()
    {
        var post = PostFactory.Create("Ab1Cd") with { Slug = new string('a', 200) };

        var name = VideoFileNames.ForPost(post);

        Assert.Equal(new string('a', 60) + "_Ab1Cd.webm", name);
    }

    [Fact]
    public void ForLocalImport_UsesOriginalNameAndHashPrefix()
    {
        Assert.Equal("my_boot_intro_local-abcdef12.webm", VideoFileNames.ForLocalImport("My Boot Intro.webm", "abcdef1234567890"));
    }

    [Theory]
    [InlineData("intro_MnZgE.webm", true)]
    [InlineData("INTRO.WEBM", true)]
    [InlineData("intro.mp4", false)]
    [InlineData(".webm", false)]
    [InlineData(".hidden.webm", false)]
    [InlineData("../intro.webm", false)]
    [InlineData("dir\\intro.webm", false)]
    [InlineData("C:intro.webm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeFileName_AcceptsOnlyBareWebmNames(string? name, bool expected)
    {
        Assert.Equal(expected, VideoFileNames.IsSafeFileName(name));
    }
}
