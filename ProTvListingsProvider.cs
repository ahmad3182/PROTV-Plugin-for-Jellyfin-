using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace PROTV.Plugin;

public sealed class ProTvListingsProvider : IListingsProvider
{
    private readonly ProTvApiClient _api;

    public ProTvListingsProvider(ProTvApiClient api)
    {
        _api = api;
    }

    public string Name => "PROTV EPG";

    public string Type => "PROTV";

    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(
        ListingsProviderInfo info,
        string channelId,
        DateTime startDateUtc,
        DateTime endDateUtc,
        CancellationToken cancellationToken)
    {
        var channels = await _api.GetEpgAsync(cancellationToken);
        var channel = channels.FirstOrDefault(item => item.ChannelId.ToString() == channelId);
        return (channel?.Programs ?? Array.Empty<ProTvEpgItem>())
            .Where(program => program.EndTimeUtc > startDateUtc && program.StartTimeUtc < endDateUtc)
            .Select(program => new ProgramInfo
            {
                Id = $"PROTV-{channelId}-{program.StartTimeUtc:O}",
                ChannelId = channelId,
                Name = program.Title ?? "PROTV",
                Overview = program.Description,
                StartDate = program.StartTimeUtc,
                EndDate = program.EndTimeUtc,
                IsLive = true
            });
    }

    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
    {
        return validateListings ? _api.GetEpgAsync(CancellationToken.None) : Task.CompletedTask;
    }

    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
    {
        return Task.FromResult(new List<NameIdPair>
        {
            new() { Name = "PROTV", Id = "PROTV" }
        });
    }

    public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        var channels = await _api.GetChannelsAsync(cancellationToken);
        return ProTvChannelOrdering.Sort(channels).Select((channel, index) => new ChannelInfo
        {
            Id = channel.Id.ToString(),
            Name = ProTvChannelOrdering.DisplayName(channel),
            Number = (index + 1).ToString(),
            ImageUrl = ProTvChannelOrdering.LogoUrl(channel),
            HasImage = !string.IsNullOrWhiteSpace(ProTvChannelOrdering.LogoUrl(channel)),
            ChannelType = ChannelType.TV,
            IsFavorite = ProTvChannelOrdering.IsFavorite(channel.Id),
            ChannelGroup = ProTvChannelOrdering.GroupName(channel.Id)
        }).ToList();
    }
}
