using System.Globalization;
using System.Text.Json;

namespace MediaPager.App.Api.Services.Tools;

internal sealed record FfmpegAudioProbeResult(
    long? DurationMicros,
    IReadOnlyList<FfmpegAudioStream> AudioStreams);

internal sealed record FfmpegAudioStream(
    int AudioOrdinal,
    string? Language,
    string? Title,
    IReadOnlyList<string> Dispositions);

internal sealed record FfmpegOutputAudioStream(
    int InputAudioOrdinal,
    string Title,
    string? Language,
    IReadOnlyList<string> Dispositions);

internal static class FfmpegAudioTrackPolicy
{
    private static readonly HashSet<string> UnknownValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "und", "unknown", "undefined", "none", "null", "n/a", "na", "-",
        "soundhandler", "audiohandler", "audio",
    };

    public static FfmpegAudioProbeResult? ParseProbeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return null;

            long? durationMicros = null;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationElement))
            {
                var durationText = durationElement.ValueKind switch
                {
                    JsonValueKind.String => durationElement.GetString(),
                    JsonValueKind.Number => durationElement.GetRawText(),
                    _ => null,
                };
                if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                    durationMicros = (long)(seconds * 1_000_000);
            }

            var audioStreams = new List<FfmpegAudioStream>();
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
                    continue;

                var tags = stream.TryGetProperty("tags", out var tagsElement)
                    ? tagsElement
                    : default;
                var language = ReadTag(tags, "language");
                var title = ReadTag(tags, "title");
                var dispositions = stream.TryGetProperty("disposition", out var dispositionElement)
                    ? ReadDispositions(dispositionElement)
                    : [];

                audioStreams.Add(new FfmpegAudioStream(
                    audioStreams.Count,
                    language,
                    title,
                    dispositions));
            }

            return new FfmpegAudioProbeResult(durationMicros, audioStreams);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<FfmpegOutputAudioStream>? SelectForOutput(
        IReadOnlyList<FfmpegAudioStream>? sourceStreams)
    {
        // A failed probe is different from a successful probe finding no audio. The caller
        // keeps FFmpeg's original optional audio mapping when metadata is unavailable.
        if (sourceStreams is null)
            return null;
        if (sourceStreams.Count == 0)
            return [];

        var hasDescriptiveTrack = sourceStreams.Any(HasDescriptiveLabel);
        List<FfmpegAudioStream> selected;
        if (hasDescriptiveTrack)
            selected = sourceStreams.Where(stream => stream.IsDefault() || HasDescriptiveLabel(stream)).ToList();
        else
            selected = [sourceStreams.FirstOrDefault(stream => stream.IsDefault()) ?? sourceStreams[0]];
        if (selected.Count == 0)
            selected.Add(sourceStreams[0]);

        var defaultIndex = selected.FindIndex(stream => stream.IsDefault());
        if (defaultIndex < 0)
            defaultIndex = 0;

        return selected.Select((stream, outputIndex) =>
        {
            var dispositions = stream.Dispositions
                .Where(disposition => !string.Equals(disposition, "default", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (outputIndex == defaultIndex)
                dispositions.Add("default");

            return new FfmpegOutputAudioStream(
                stream.AudioOrdinal,
                OutputTitle(stream, hasDescriptiveTrack),
                HasMeaningfulValue(stream.Language) ? stream.Language!.Trim() : null,
                dispositions);
        }).ToList();
    }

    public static string[] MappingArguments(IReadOnlyList<FfmpegOutputAudioStream>? outputStreams)
    {
        if (outputStreams is null)
            return ["-map", "0:a?"];

        return outputStreams
            .SelectMany(stream => new[] { "-map", $"0:a:{stream.InputAudioOrdinal}" })
            .ToArray();
    }

    public static string[] MetadataArguments(IReadOnlyList<FfmpegOutputAudioStream>? outputStreams)
    {
        if (outputStreams is null || outputStreams.Count == 0)
            return [];

        var arguments = new List<string>();
        for (var outputIndex = 0; outputIndex < outputStreams.Count; outputIndex++)
        {
            var stream = outputStreams[outputIndex];
            arguments.Add($"-metadata:s:a:{outputIndex}");
            arguments.Add($"title={stream.Title}");
            if (HasMeaningfulValue(stream.Language))
            {
                arguments.Add($"-metadata:s:a:{outputIndex}");
                arguments.Add($"language={stream.Language}");
            }
            arguments.Add($"-disposition:a:{outputIndex}");
            arguments.Add(stream.Dispositions.Count == 0 ? "0" : string.Join('+', stream.Dispositions));
        }

        return arguments.ToArray();
    }

    private static bool HasDescriptiveLabel(FfmpegAudioStream stream) =>
        HasMeaningfulValue(stream.Title) || HasMeaningfulValue(stream.Language);

    private static bool IsDefault(this FfmpegAudioStream stream) =>
        stream.Dispositions.Any(disposition =>
            string.Equals(disposition, "default", StringComparison.OrdinalIgnoreCase));

    private static string OutputTitle(FfmpegAudioStream stream, bool hasDescriptiveTrack)
    {
        if (HasMeaningfulValue(stream.Title)) return stream.Title!.Trim();
        if (HasMeaningfulValue(stream.Language)) return LanguageName(stream.Language!);
        return hasDescriptiveTrack ? $"Audio track {stream.AudioOrdinal + 1}" : "Default audio";
    }

    private static string LanguageName(string language)
    {
        var value = language.Trim();
        try
        {
            return CultureInfo.GetCultureInfo(value).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            var baseLanguage = value.Split('-', '_')[0];
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.ThreeLetterISOLanguageName, baseLanguage, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate.TwoLetterISOLanguageName, baseLanguage, StringComparison.OrdinalIgnoreCase));
            return culture?.EnglishName ?? value;
        }
    }

    private static bool HasMeaningfulValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !UnknownValues.Contains(value.Trim());

    private static string? ReadTag(JsonElement tags, string name) =>
        tags.ValueKind == JsonValueKind.Object &&
        tags.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadDispositions(JsonElement disposition)
    {
        if (disposition.ValueKind != JsonValueKind.Object) return [];
        return disposition.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.True ||
                property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var enabled) && enabled != 0)
            .Select(property => property.Name)
            .ToList();
    }
}
