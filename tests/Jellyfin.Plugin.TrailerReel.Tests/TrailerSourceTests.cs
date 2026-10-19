using System.Net;
using System.Text;
using Jellyfin.Plugin.TrailerReel.Configuration;
using Jellyfin.Plugin.TrailerReel.Models;
using Jellyfin.Plugin.TrailerReel.Services;

internal static class TrailerSourceTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var format = TrailerSourceRejectedException.FromYtDlpError(
            "ERROR: [youtube] FwoYYKyJo1g: Requested format is not available. Use --list-formats for a list of available formats");
        check("reported format error is a source rejection", format?.Reason == TrailerSourceRejection.FormatUnavailable);
        check("missing format alone does not claim a challenge failure", format?.ExtractionWarning == false);
        var age = TrailerSourceRejectedException.FromYtDlpError(
            "ERROR: [youtube] EfluEyQ5QIA: Sign in to confirm your age. Use --cookies-from-browser or --cookies for the authentication.");
        check("reported age gate is classified separately", age?.Reason == TrailerSourceRejection.AgeRestricted);
        var challenge = TrailerSourceRejectedException.FromYtDlpError(
            "WARNING: [youtube] n challenge solving failed: Some formats may be missing\nERROR: Requested format is not available");
        check("format failure retains the presence of extractor warnings", challenge?.ExtractionWarning == true);
        check("unrecognized failures stay unexpected", TrailerSourceRejectedException.FromYtDlpError("ERROR: ffmpeg not found") is null);
        check("bot sign-in is not mislabeled as an age restriction",
            TrailerSourceRejectedException.FromYtDlpError("ERROR: Sign in to confirm you're not a bot") is null);

        var tried = new List<string>();
        var rejected = new List<TrailerSourceRejection>();
        var sources = new[] { Video("format"), Video("format"), Video("age"), Video("ok"), Video("unused") };
        var result = await TrailerSourceAcquirer.TryAcquireAsync(sources, (video, _) =>
        {
            tried.Add(video.Key);
            return video.Key switch
            {
                "format" => throw format!,
                "age" => throw age!,
                _ => Task.FromResult("Movie-trailer.mp4"),
            };
        }, (_, ex) => rejected.Add(ex.Reason), CancellationToken.None);
        check("alternative upload succeeds after format and age rejections",
            result?.Video.Key == "ok" && result.FileName == "Movie-trailer.mp4"
                && tried.SequenceEqual(["format", "age", "ok"]));
        check("each rejected source is reported once",
            rejected.SequenceEqual([TrailerSourceRejection.FormatUnavailable, TrailerSourceRejection.AgeRestricted]));

        var limitedAttempts = 0;
        var exhausted = await TrailerSourceAcquirer.TryAcquireAsync(
            Enumerable.Range(1, 10).Select(id => Video(id.ToString())).ToArray(),
            (_, _) => { limitedAttempts++; throw age!; }, (_, _) => { }, CancellationToken.None);
        check("source attempts are bounded and exhausted movies are skipped",
            exhausted is null && limitedAttempts == 3);
        var unexpectedAttempts = 0;
        try
        {
            await TrailerSourceAcquirer.TryAcquireAsync(sources,
                (_, _) => { unexpectedAttempts++; throw new IOException("disk full"); },
                (_, _) => check("disk errors cannot become source skips", false), CancellationToken.None);
            check("unexpected acquisition failure must propagate", false);
        }
        catch (IOException)
        {
            check("unexpected acquisition failure propagates without blind retries", unexpectedAttempts == 1);
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await TrailerSourceAcquirer.TryAcquireAsync(sources,
                (_, _) => { check("cancellation prevents download", false); return Task.FromResult("unused"); },
                (_, _) => { }, cancelled.Token);
            check("source cancellation must propagate", false);
        }
        catch (OperationCanceledException)
        {
            check("source cancellation propagates", true);
        }

        using var rankingHandler = new VideoHandler(_ => """
            {"results":[
              {"key":"low","site":"YouTube","type":"Trailer","official":true,"size":720},
              {"key":"unofficial","site":"YouTube","type":"Trailer","official":false,"size":1080},
              {"key":"old","site":"YouTube","type":"Trailer","official":true,"size":1080,"published_at":"2026-01-01T00:00:00Z"},
              {"key":"new","site":"YouTube","type":"Trailer","official":true,"size":1080,"published_at":"2026-02-01T00:00:00Z"},
              {"key":"new","site":"YouTube","type":"Trailer","official":true,"size":1080},
              {"key":"clip","site":"YouTube","type":"Clip","official":true,"size":1080}
            ]}
            """);
        var config = new PluginConfiguration { TmdbReadAccessToken = "test-token" };
        using var httpClient = new HttpClient(rankingHandler);
        var ranked = await new TmdbClient(httpClient).GetTrailerCandidatesAsync(1, config, CancellationToken.None);
        check("alternate sources preserve official trailer filtering, deduplication and ranking",
            ranked.Select(video => video.Key).SequenceEqual(["new", "old", "low"]));

        using var fallbackHandler = new VideoHandler(uri => uri.Query.Contains("language=")
            ? """{"results":[{"key":"fan","site":"YouTube","type":"Trailer","official":false,"size":1080}]}"""
            : """{"results":[{"key":"studio","site":"YouTube","type":"Trailer","official":true,"size":1080}]}""");
        using var fallbackClient = new HttpClient(fallbackHandler);
        var fallback = await new TmdbClient(fallbackClient).GetTrailerCandidatesAsync(2, config, CancellationToken.None);
        check("localized unofficial-only results still search the official fallback",
            fallbackHandler.Requests == 2 && fallback.Select(video => video.Key).SequenceEqual(["studio"]));
    }

    private static TrailerVideo Video(string key) => new(key, key, true, "YouTube", "Trailer", 1080, null);

    private sealed class VideoHandler(Func<Uri, string> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request.RequestUri!), Encoding.UTF8, "application/json"),
            });
        }
    }
}
