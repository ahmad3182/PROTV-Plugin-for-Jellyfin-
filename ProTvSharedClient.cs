using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PROTV.Plugin;

// One PROTV API client for the whole plugin (tuner, guide, home page tab and VOD). The login
// tokens live inside the client, so a client per call meant a login per call; sharing one means
// one login, then token reuse and refresh. It reads the saved settings live and drops its tokens
// when they change.
internal static class ProTvSharedClient
{
    private static readonly object Gate = new();

    // The connect timeout is what makes an unreachable PROTV (not on the Syrian network, cable out)
    // show up in seconds: without it a connection attempt waits for HttpClient's 100 s limit, and the
    // service could not be called "unavailable" for minutes.
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) });
    private static ProTvApiClient? _client;

    // The first caller creates the client; everyone after gets the same one. The httpClient argument
    // is ignored on purpose: whichever caller came first would otherwise decide whether the client
    // has the connect timeout above (the service registrator still passes a plain `new HttpClient()`).
    public static ProTvApiClient GetOrCreate(
        HttpClient? httpClient,
        ILogger<ProTvApiClient>? logger,
        Func<PluginConfiguration>? configurationFactory)
    {
        lock (Gate)
        {
            _client ??= new ProTvApiClient(
                SharedHttp,
                new ProTvRecordingLogger(logger ?? NullLogger<ProTvApiClient>.Instance),
                configurationFactory ?? (() => Plugin.Instance?.Configuration ?? new PluginConfiguration()));
            return _client;
        }
    }

    public static ProTvApiClient Get() => GetOrCreate(null, null, null);
}
