using System.Text.Json.Serialization;

namespace PROTV.Plugin;

// Data transfer objects for the PROTV VOD API (Movies / Series / Seasons / Episodes).
//
// PROTV answers camelCase; System.Net.Http.Json (JsonSerializerDefaults.Web) matches it
// case-insensitively, so the plain PascalCase names below bind as-is. Only the fields the plugin
// actually uses are declared, and everything the API can leave out is nullable.

public sealed class ProTvVodLink
{
    public int Id { get; set; }

    // The HLS link (http(s)://host:1935/.../playlist.m3u8) - AES encrypted in the response.
    public string? Path { get; set; }

    // The RTSP link (rtsp://host:1936/...) - AES encrypted in the response.
    public string? PathRTSP { get; set; }

    public string? DeviceType { get; set; }

    public string? Quality { get; set; }

    public string? Status { get; set; }
}

public sealed class ProTvVodSubtitle
{
    public int Id { get; set; }

    // The subtitle file, relative to the PROTV content host and AES encrypted in the response.
    public string? Path { get; set; }

    public string? Language { get; set; }

    public string? LanguageAr { get; set; }
}

public sealed class ProTvVodCategory
{
    public int Id { get; set; }

    public string? TitleEn { get; set; }

    public string? TitleAr { get; set; }
}

public sealed class ProTvMovie
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? SecondaryTitle { get; set; }

    // Minutes as text ("131") in movie responses; the length as hh:mm:ss is in DurationTime.
    public string? Duration { get; set; }

    public string? DurationTime { get; set; }

    public int? ReleaseYear { get; set; }

    public string? PlotEn { get; set; }

    public string? PlotAr { get; set; }

    public string? Poster { get; set; }

    public string? MediaCost { get; set; }

    public int Price { get; set; }

    public string? Status { get; set; }

    public string? Tags { get; set; }

    public string? TrailerUrl { get; set; }

    public string? AudienceRating { get; set; }

    public string? AudienceRatingString { get; set; }

    public double ImdbScore { get; set; }

    public double ProtvScore { get; set; }

    public bool IsHot { get; set; }

    public bool IsNew { get; set; }

    // Only filled by the detail call (api/Movies/{id}); the list responses send an empty array.
    public IReadOnlyList<ProTvVodLink>? MovieLinks { get; set; }

    public IReadOnlyList<ProTvVodSubtitle>? Subtitles { get; set; }

    public IReadOnlyList<ProTvVodCategory>? Categories { get; set; }
}

public sealed class ProTvSeries
{
    public int Id { get; set; }

    public string? Title { get; set; }

    public string? SecondaryTitle { get; set; }

    public string? Poster { get; set; }

    public string? DescriptionEn { get; set; }

    public string? DescriptionAr { get; set; }

    public string? Tags { get; set; }

    public string? AudienceRating { get; set; }

    public string? AudienceRatingString { get; set; }

    public double ImdbScore { get; set; }

    public int MoviesCount { get; set; }

    public int SeasonsCount { get; set; }

    public bool IsHot { get; set; }

    public bool IsNew { get; set; }

    public IReadOnlyList<ProTvMovie>? Movies { get; set; }

    public IReadOnlyList<ProTvSeason>? Seasons { get; set; }

    public IReadOnlyList<ProTvVodCategory>? Categories { get; set; }
}

public sealed class ProTvSeason
{
    public int Id { get; set; }

    public int Index { get; set; }

    public string? Title { get; set; }

    public string? SecondaryTitle { get; set; }

    public string? Poster { get; set; }

    public string? EpisodePoster { get; set; }

    public string? PlotEn { get; set; }

    public string? PlotAr { get; set; }

    public int? ReleaseYear { get; set; }

    public int EpisodeCount { get; set; }

    public int SeriesId { get; set; }

    public string? SeriesTitle { get; set; }

    public double ImdbScore { get; set; }

    public IReadOnlyList<ProTvEpisode>? Episodes { get; set; }
}

public sealed class ProTvEpisode
{
    public int Id { get; set; }

    public int Index { get; set; }

    public string? Title { get; set; }

    // Episodes report the length as hh:mm:ss here (movies use minutes); both are parsed defensively.
    public string? Duration { get; set; }

    public string? DurationTime { get; set; }

    public int? ReleaseYear { get; set; }

    public string? DescrptionEn { get; set; }

    public string? DescrptionAr { get; set; }

    public string? Poster { get; set; }

    public string? MediaCost { get; set; }

    public int Price { get; set; }

    public int SeriesId { get; set; }

    public int SeasonId { get; set; }

    public string? SeasonTitle { get; set; }

    // PROTV sends this as text ("1").
    public string? SeasonIndex { get; set; }

    public string? SeriesTitle { get; set; }

    public double ImdbScore { get; set; }

    // Only filled by the detail call (api/Episodes/{id}); the list responses omit the field.
    public IReadOnlyList<ProTvVodLink>? EpisodeLinks { get; set; }

    public IReadOnlyList<ProTvVodSubtitle>? Subtitles { get; set; }
}
