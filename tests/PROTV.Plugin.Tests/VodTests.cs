using System.Xml.Linq;

namespace PROTV.Plugin.Tests;

public sealed class VodSweepTests
{
    [Theory]
    [InlineData(100, 0, false)]   // previous count unknown
    [InlineData(100, 100, true)]
    [InlineData(100, 50, true)]
    [InlineData(49, 100, false)]
    [InlineData(0, 100, false)]
    [InlineData(0, 0, false)]
    public void A_sweep_needs_a_complete_and_plausible_listing(int listed, int previous, bool expected)
    {
        Assert.Equal(expected, ProTvVodLibrary.IsPlausibleListing(listed, previous, "films"));
    }

    [Fact]
    public void Nfo_needs_genres_when_missing_or_written_without_them()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protv-nfo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "movie.nfo");

        try
        {
            Assert.True(ProTvVodLibrary.NfoNeedsGenres(path));

            File.WriteAllText(path, "<?xml version=\"1.0\"?><movie><title>No genres</title></movie>");
            Assert.True(ProTvVodLibrary.NfoNeedsGenres(path));

            File.WriteAllText(path, "<?xml version=\"1.0\"?><movie><genre>Drama</genre></movie>");
            Assert.False(ProTvVodLibrary.NfoNeedsGenres(path));

            File.WriteAllText(path, "<?xml version=\"1.0\"?><movie><GENRE>Drama</GENRE></movie>");
            Assert.False(ProTvVodLibrary.NfoNeedsGenres(path));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A failed cleanup must not hide the test result.
            }
        }
    }

    [Fact]
    public async Task Existing_stream_content_is_compared_so_rotated_links_are_rewritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protv-stream-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "movie.strm");

        try
        {
            await File.WriteAllTextAsync(path, "http://server/PROTV/Vod/Stream/movie/1/old.m3u8");
            Assert.True(await ProTvVodLibrary.FileContentEqualsAsync(
                path, "http://server/PROTV/Vod/Stream/movie/1/old.m3u8"));
            Assert.False(await ProTvVodLibrary.FileContentEqualsAsync(
                path, "http://server/PROTV/Vod/Stream/movie/1/new.m3u8"));
            Assert.False(await ProTvVodLibrary.FileContentEqualsAsync(
                Path.Combine(directory, "missing.strm"), "anything"));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A failed cleanup must not hide the test result.
            }
        }
    }

    [Fact]
    public void Existing_genres_are_read_back_for_a_forced_rewrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protv-genre-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "movie.nfo");

        try
        {
            Assert.Null(ProTvVodLibrary.ExistingGenres(path));

            File.WriteAllText(
                path,
                "<?xml version=\"1.0\"?><movie><genre>Drama</genre><genre>دراما</genre><genre>Drama</genre></movie>");
            var genres = ProTvVodLibrary.ExistingGenres(path);

            Assert.NotNull(genres);
            Assert.Equal(new[] { "Drama", "دراما" }, genres!.Select(genre => genre.TitleEn));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A failed cleanup must not hide the test result.
            }
        }
    }

    [Fact]
    public void Genres_are_written_once_with_english_preferred_and_arabic_fallback()
    {
        var root = new XElement("movie");
        var categories = new[]
        {
            new ProTvVodCategory { Id = 1, TitleEn = "Drama", TitleAr = "دراما" },
            new ProTvVodCategory { Id = 2, TitleEn = "Drama" },
            new ProTvVodCategory { Id = 3, TitleAr = "كوميديا" },
            new ProTvVodCategory { Id = 4, TitleEn = "  " }
        };

        ProTvVodLibrary.AddGenres(root, categories);

        Assert.Equal(new[] { "Drama", "كوميديا" }, root.Elements("genre").Select(element => element.Value));
        Assert.Equal(2, root.Elements("genre").Count());
    }

    [Fact]
    public void AddGenres_accepts_no_categories()
    {
        var root = new XElement("movie");

        ProTvVodLibrary.AddGenres(root, null);
        ProTvVodLibrary.AddGenres(root, Array.Empty<ProTvVodCategory>());

        Assert.Empty(root.Elements("genre"));
    }
}
