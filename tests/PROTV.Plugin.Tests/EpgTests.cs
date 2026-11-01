using System.Text.Json;

namespace PROTV.Plugin.Tests;

public sealed class EpgTests
{
    private const string SampleJson = """
        {
          "status": true,
          "serverTimeLocal": "2026-10-23T02:57:22+03:00",
          "serverTimeUTC": "2026-10-22T23:57:22Z",
          "data": [
            {
              "channelId": 7,
              "channelTitleEn": "One",
              "channelTitleAr": "???",
              "epgItems": [
                {
                  "id": 1,
                  "title": "Evening News",
                  "description": "News",
                  "day": "2026-10-23T00:00:00",
                  "startTime": "00:30:00",
                  "showTime": "2026-10-23T00:30:00",
                  "duration": "120"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Epg_items_are_read_from_the_live_epgItems_shape()
    {
        var response = JsonSerializer.Deserialize<ProTvEpgResponse>(
            SampleJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var channel = Assert.Single(response.Data!);
        Assert.Equal(7, channel.ChannelId);
        Assert.Equal("One", channel.ChannelName);
        var program = Assert.Single(channel.Programs!);
        Assert.Equal("Evening News", program.Title);

        var offset = ProTvApiClient.ServerOffset(response);
        Assert.Equal(TimeSpan.FromHours(3), offset);
        program.ServerOffset = offset;

        Assert.Equal(new DateTime(2026, 10, 22, 21, 30, 0, DateTimeKind.Utc), program.StartTimeUtc);
        Assert.Equal(new DateTime(2026, 10, 22, 23, 30, 0, DateTimeKind.Utc), program.EndTimeUtc);
    }

    [Fact]
    public void Epg_falls_back_to_day_and_start_time_when_show_time_is_missing()
    {
        const string json = """
            {
              "data": [
                {
                  "channelId": 7,
                  "epgItems": [
                    {
                      "title": "Late Show",
                      "day": "2026-10-23T00:00:00",
                      "startTime": "23:00:00",
                      "duration": "02:00:00"
                    }
                  ]
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<ProTvEpgResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var program = Assert.Single(Assert.Single(response.Data!).Programs!);
        program.ServerOffset = TimeSpan.FromHours(3);

        Assert.Equal(new DateTime(2026, 10, 23, 20, 0, 0, DateTimeKind.Utc), program.StartTimeUtc);
        Assert.Equal(new DateTime(2026, 10, 23, 22, 0, 0, DateTimeKind.Utc), program.EndTimeUtc);
    }

    [Fact]
    public void Epg_without_a_start_time_is_not_treated_as_on_now()
    {
        var item = new ProTvEpgItem { Title = "Unknown" };

        Assert.Equal(DateTime.MinValue, item.StartTimeUtc);
        Assert.Equal(DateTime.MinValue, item.EndTimeUtc);
    }
}
