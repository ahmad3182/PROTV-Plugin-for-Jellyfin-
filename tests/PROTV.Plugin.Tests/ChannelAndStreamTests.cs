using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace PROTV.Plugin.Tests;

public sealed class ChannelOrderingTests
{
    [Fact]
    public void Sort_hides_channels_and_orders_by_priority_then_name()
    {
        var channels = new[]
        {
            new ProTvChannel { Id = 1, TitleEn = "Beta" },
            new ProTvChannel { Id = 2, TitleEn = "Alpha" },
            new ProTvChannel { Id = 3, TitleEn = "Gamma" },
            new ProTvChannel { Id = 4, TitleEn = "Delta" },
            new ProTvChannel { Id = 5, TitleEn = "Beta" }
        };
        var overrides = new[]
        {
            new ProTvChannelOverride { ChannelId = 1, CustomName = "Zulu", Priority = 2 },
            new ProTvChannelOverride { ChannelId = 2, Hidden = true },
            new ProTvChannelOverride { ChannelId = 3, Priority = -1 }
        };

        var sorted = ProTvChannelOrdering.Sort(channels, overrides);

        Assert.Equal(new[] { 3, 5, 4, 1 }, sorted.Select(channel => channel.Id));
    }

    [Fact]
    public void The_last_override_for_a_channel_wins()
    {
        var channels = new[] { new ProTvChannel { Id = 1, TitleEn = "One" }, new ProTvChannel { Id = 2, TitleEn = "Two" } };
        var overrides = new[]
        {
            new ProTvChannelOverride { ChannelId = 1, Priority = 10 },
            new ProTvChannelOverride { ChannelId = 1, Priority = -10 }
        };

        var sorted = ProTvChannelOrdering.Sort(channels, overrides);

        Assert.Equal(1, sorted[0].Id);
    }

    [Fact]
    public void Display_name_and_logo_prefer_the_dashboard_override()
    {
        var channel = new ProTvChannel { Id = 7, TitleEn = "Original", TitleAr = "الأصلية", Logo = "https://example.test/original.png" };
        var overrides = new Dictionary<int, ProTvChannelOverride>
        {
            [7] = new()
            {
                ChannelId = 7,
                CustomName = "Custom",
                LogoUrl = "https://example.test/custom.png"
            }
        };

        Assert.Equal("Custom", ProTvChannelOrdering.DisplayName(channel, overrides));
        Assert.Equal("https://example.test/custom.png", ProTvChannelOrdering.LogoUrl(channel, overrides));
    }

    [Fact]
    public void Display_name_falls_back_to_english_then_arabic_then_the_id()
    {
        var overrides = new Dictionary<int, ProTvChannelOverride>();

        Assert.Equal("English", ProTvChannelOrdering.DisplayName(
            new ProTvChannel { Id = 1, TitleEn = "English", TitleAr = "عربي" }, overrides));
        Assert.Equal("عربي", ProTvChannelOrdering.DisplayName(
            new ProTvChannel { Id = 2, TitleAr = "عربي" }, overrides));
        Assert.Equal("Channel 3", ProTvChannelOrdering.DisplayName(
            new ProTvChannel { Id = 3 }, overrides));
    }
}

public sealed class StreamContractTests
{
    [Fact]
    public void Live_stream_implements_the_live_tv_and_direct_stream_contracts()
    {
        using var stream = new ProTvLiveStream("http://127.0.0.1:8080/live.ts", "12", "tuner");

        Assert.IsAssignableFrom<ILiveStream>(stream);
        Assert.IsAssignableFrom<IDirectStreamProvider>(stream);
        Assert.Equal("PROTV-12", stream.UniqueId);
        Assert.Equal("PROTV-12", ProTvLiveStream.BuildUniqueId("12"));
    }

    [Fact]
    public void Http_stream_is_marked_for_jellyfin_transcoding_not_direct_play()
    {
        using var stream = new ProTvLiveStream("http://127.0.0.1:8080/live.ts", "12", "tuner");

        Assert.Equal(MediaProtocol.Http, stream.MediaSource.Protocol);
        Assert.False(stream.MediaSource.SupportsDirectPlay);
        Assert.True(stream.MediaSource.SupportsDirectStream);
        Assert.True(stream.MediaSource.SupportsTranscoding);
    }

    [Fact]
    public void Multicast_stream_uses_udp_and_still_goes_through_jellyfin()
    {
        using var stream = new ProTvLiveStream("udp://239.0.0.1:5000", "12", "tuner");

        Assert.Equal(MediaProtocol.Udp, stream.MediaSource.Protocol);
        Assert.False(stream.MediaSource.SupportsDirectPlay);
    }

    [Theory]
    [InlineData("udp://not-an-ip:5000")]
    [InlineData("udp://127.0.0.1:5000")]
    [InlineData("udp://239.0.0.1:0")]
    public async Task Invalid_multicast_urls_are_rejected_before_opening_a_socket(string url)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MulticastStream.OpenAsync(url, CancellationToken.None));
    }
}
