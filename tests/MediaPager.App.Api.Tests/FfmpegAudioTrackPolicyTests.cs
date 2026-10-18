using System.Text.Json;
using MediaPager.App.Api.Services.Tools;
using Xunit;

namespace MediaPager.App.Api.Tests;

public sealed class FfmpegAudioTrackPolicyTests
{
    [Fact]
    public void All_unknown_tracks_reduce_to_the_default_track()
    {
        var source = new[]
        {
            new FfmpegAudioStream(0, "und", "Unknown", []),
            new FfmpegAudioStream(1, "und", "Unknown", ["default"]),
            new FfmpegAudioStream(2, null, null, []),
        };

        var selected = FfmpegAudioTrackPolicy.SelectForOutput(source);

        var track = Assert.Single(selected!);
        Assert.Equal(1, track.InputAudioOrdinal);
        Assert.Equal("Default audio", track.Title);
        Assert.Contains("default", track.Dispositions);
    }

    [Fact]
    public void All_unknown_tracks_without_a_default_keep_the_first()
    {
        var source = new[]
        {
            new FfmpegAudioStream(0, null, null, []),
            new FfmpegAudioStream(1, "unknown", "SoundHandler", []),
        };

        var selected = FfmpegAudioTrackPolicy.SelectForOutput(source);

        var track = Assert.Single(selected!);
        Assert.Equal(0, track.InputAudioOrdinal);
        Assert.Contains("default", track.Dispositions);
    }

    [Fact]
    public void Mixed_tracks_keep_descriptive_tracks_and_the_default()
    {
        var source = new[]
        {
            new FfmpegAudioStream(0, "und", "Unknown", ["default"]),
            new FfmpegAudioStream(1, "rus", "Russian", []),
            new FfmpegAudioStream(2, "und", null, []),
            new FfmpegAudioStream(3, "eng", "English Director's Cut", []),
        };

        var selected = FfmpegAudioTrackPolicy.SelectForOutput(source)!;

        Assert.Equal(3, selected.Count);
        Assert.Equal(new[] { 0, 1, 3 }, selected.Select(track => track.InputAudioOrdinal));
        Assert.Equal("Audio track 1", selected[0].Title);
        Assert.Equal("Russian", selected[1].Title);
        Assert.Equal("English Director's Cut", selected[2].Title);
        Assert.Contains("default", selected[0].Dispositions);
        Assert.DoesNotContain("default", selected[1].Dispositions);
    }

    [Fact]
    public void Probe_parser_returns_audio_ordinals_and_default_metadata()
    {
        const string json = """
            {
              "streams": [
                { "index": 0, "codec_type": "video" },
                { "index": 1, "codec_type": "audio", "tags": { "language": "eng", "title": "English" }, "disposition": { "default": 1, "comment": 0 } },
                { "index": 2, "codec_type": "audio", "tags": { "language": "rus" }, "disposition": { "default": 0, "comment": 1 } }
              ],
              "format": { "duration": "12.5" }
            }
            """;

        var probe = FfmpegAudioTrackPolicy.ParseProbeJson(json);

        Assert.NotNull(probe);
        Assert.Equal(12_500_000, probe.DurationMicros);
        Assert.Equal(2, probe.AudioStreams.Count);
        Assert.Equal(0, probe.AudioStreams[0].AudioOrdinal);
        Assert.Equal("eng", probe.AudioStreams[0].Language);
        Assert.Contains("default", probe.AudioStreams[0].Dispositions);
        Assert.Equal(1, probe.AudioStreams[1].AudioOrdinal);
        Assert.Contains("comment", probe.AudioStreams[1].Dispositions);
    }

    [Fact]
    public void Failed_probe_falls_back_to_optional_audio_mapping()
    {
        Assert.Null(FfmpegAudioTrackPolicy.ParseProbeJson("invalid"));
        Assert.Equal(["-map", "0:a?"], FfmpegAudioTrackPolicy.MappingArguments(null));
        Assert.Empty(FfmpegAudioTrackPolicy.SelectForOutput([])!);
    }

    [Fact]
    public void Selected_track_arguments_keep_input_order_and_output_metadata_ordinals()
    {
        var source = new[]
        {
            new FfmpegAudioStream(0, "eng", "English", []),
            new FfmpegAudioStream(1, "rus", "Russian", ["default"]),
        };
        var selected = FfmpegAudioTrackPolicy.SelectForOutput(source)!;

        Assert.Equal(new[] { "-map", "0:a:0", "-map", "0:a:1" }, FfmpegAudioTrackPolicy.MappingArguments(selected));
        var metadata = FfmpegAudioTrackPolicy.MetadataArguments(selected);
        Assert.Contains("title=English", metadata);
        Assert.Contains("language=eng", metadata);
        Assert.Contains("title=Russian", metadata);
        Assert.Contains("language=rus", metadata);
        Assert.Contains("-disposition:a:1", metadata);
        Assert.Contains("default", metadata);
    }
}
