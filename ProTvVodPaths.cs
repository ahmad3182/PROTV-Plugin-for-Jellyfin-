using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PROTV.Plugin;

// Where a PROTV VOD item lives on disk in the plugin's own Jellyfin library.
//
// The VOD sync writes those files, and the PROTV home page tab recomputes exactly the same paths
// to find the Jellyfin item behind a PROTV id, so every name is built here and nowhere else.

internal static class ProTvVodPaths
{
    // The two virtual folders the plugin creates (and owns) inside Jellyfin: one Films library and
    // one Shows library. They are deliberately kept apart - inside one "mixed" library Jellyfin
    // reads a folder full of films as a single series.
    public const string LibraryName = "PROTV VOD";

    public const string SeriesLibraryName = "PROTV VOD Series";

    // The image written into both library folders, so the library tiles show the same icon as the
    // PROTV entry in the Jellyfin dashboard menu.
    public const string LibraryIconFileName = "folder.jpg";

    // Long PROTV titles are cut so a full path stays inside the Windows limit.
    private const int MaxNameLength = 80;

    // Every name gets a stable "[protv-<id>]" suffix, which is what makes the mapping from a PROTV
    // id to a Jellyfin item (and back) unambiguous even when two items share a title.
    private const string MarkerPrefix = " [protv-";

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    // Everything the plugin owns lives under this folder: both libraries and the sync state file.
    public static string RootDirectory(string dataPath) => Path.Combine(dataPath, "protv-vod");

    public static string MoviesDirectory(string dataPath) => Path.Combine(RootDirectory(dataPath), "Movies");

    public static string SeriesDirectory(string dataPath) => Path.Combine(RootDirectory(dataPath), "Shows");

    // What Jellyfin is pointed at: one library per kind of content, in the layout Jellyfin expects
    // (the film folders sit directly under the films library, each series folder under the shows
    // library), so a film is read as a film and a series as a series.
    public static string MoviesLibraryRoot(string dataPath) => MoviesDirectory(dataPath);

    public static string SeriesLibraryRoot(string dataPath) => SeriesDirectory(dataPath);

    public static string LibraryIconFile(string libraryRoot) => Path.Combine(libraryRoot, LibraryIconFileName);

    // Where an external subtitle for an item is stored. Jellyfin only picks a subtitle up when it
    // sits next to the video and is named "<video name>.<language><extension>".
    public static string SubtitleFile(string mediaFilePath, string language, string extension)
    {
        var directory = Path.GetDirectoryName(mediaFilePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mediaFilePath);
        return Path.Combine(directory, name + "." + language + extension);
    }

    public static string MovieDirectory(string dataPath, ProTvMovie movie) => Path.Combine(MoviesDirectory(dataPath), MovieFolder(movie));

    public static string MovieStrmFile(string dataPath, ProTvMovie movie) => Path.Combine(MovieDirectory(dataPath, movie), MovieName(movie) + ".strm");

    public static string SeriesRootDirectory(string dataPath, ProTvSeries series) => Path.Combine(SeriesDirectory(dataPath), SeriesFolder(series));

    public static string SeasonDirectory(string dataPath, ProTvSeries series, int seasonIndex) => Path.Combine(SeriesRootDirectory(dataPath, series), SeasonFolder(seasonIndex));

    public static string EpisodeStrmFile(string dataPath, ProTvSeries series, int seasonIndex, ProTvEpisode episode)
        => Path.Combine(SeasonDirectory(dataPath, series, seasonIndex), EpisodeName(series, seasonIndex, episode) + ".strm");

    public static string MovieFolder(ProTvMovie movie) => Title(movie.Title, movie.Id, movie.ReleaseYear) + Marker(movie.Id);

    // The file keeps the plain "Title (Year)" name so Jellyfin reads the title and year straight off
    // it; the marker only lives on the folder.
    public static string MovieName(ProTvMovie movie) => Title(movie.Title, movie.Id, movie.ReleaseYear);

    public static string SeriesFolder(ProTvSeries series) => Sanitize(SeriesName(series)) + Marker(series.Id);

    public static string SeasonFolder(int seasonIndex) => "Season " + seasonIndex.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    public static string EpisodeName(ProTvSeries series, int seasonIndex, ProTvEpisode episode)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} S{1:00}E{2:00}",
            SeriesName(series),
            seasonIndex,
            episode.Index > 0 ? episode.Index : 1);

    // The name Jellyfin should show for an episode. PROTV labels an episode either with its real
    // name ("Love Boat"), with its numbers alone ("S01E01"), or - most of the time - with both
    // ("Love Boat - S01_E16", "Ashr_Alaf_khotwa_S01E01"). The numbers at the end are dropped, so
    // the real name is kept when there is one; when nothing but the (often slugged) series name is
    // left, the series name and the numbers are used instead, which reads far better in a library
    // than something like "11_22_63_S01E01".
    public static string EpisodeTitle(ProTvSeries series, int seasonIndex, ProTvEpisode episode)
    {
        var title = episode.Title?.Trim();
        if (!string.IsNullOrWhiteSpace(title))
        {
            var name = StripEpisodeNumbers(title!).Trim().Trim('-', '_', '.', ':').Trim();
            if (name.Length > 0
                && !name.Contains('_')
                && (string.IsNullOrWhiteSpace(series.Title)
                    || !string.Equals(LettersAndDigits(name), LettersAndDigits(series.Title!), StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        return EpisodeName(series, seasonIndex, episode);
    }

    // Removes the "S01E01" / "S01_E16" tail and whatever separates it from the name.
    private static string StripEpisodeNumbers(string title)
        => Regex.Replace(
            title,
            @"(?:\s*[-\u2013\u2014_:]\s*)?[Ss]\d{1,3}[_ ]?[Ee]\d{1,4}\s*$",
            string.Empty,
            RegexOptions.IgnoreCase);

    private static string LettersAndDigits(string value)
        => new(value.Where(char.IsLetterOrDigit).ToArray());

    public static string SeriesName(ProTvSeries series) => Sanitize(series.Title ?? string.Empty, $"PROTV {series.Id}");

    public static string Marker(int id) => MarkerPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    public static string Title(string? title, int id, int? year)
    {
        var name = Sanitize(title ?? string.Empty, $"PROTV {id}");
        return year is > 0 ? $"{name} ({year.Value})" : name;
    }

    // Windows forbids a handful of characters in a file name, and leading/trailing dots and spaces
    // confuse both the file system and Jellyfin's title parser.
    public static string Sanitize(string value, string fallback = "PROTV")
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(InvalidNameChars, character) >= 0 ? ' ' : character);
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('.');
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength].Trim().Trim('.');
        }

        if (cleaned.Length == 0 || cleaned is "." or "..")
        {
            cleaned = fallback;
        }

        return cleaned;
    }
}
