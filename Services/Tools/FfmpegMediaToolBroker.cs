using System.Diagnostics;
using System.Globalization;
using MediaPager.App.PluginContracts;

namespace MediaPager.App.Api.Services.Tools;

/// <summary>Host-owned, typed FFmpeg operations for plugins. Callers provide a source URI,
/// catalog id, and profile; they cannot choose executables, arguments, or filesystem paths.</summary>
public sealed class FfmpegMediaToolBroker(
    FfmpegToolInstaller tools,
    ManagedToolDirectory toolDirectory,
    ManagedToolsBootstrapState bootstrapState,
    ILibraryStore library,
    PluginRegistry registry,
    ILogger<FfmpegMediaToolBroker> logger) : IMediaToolBroker
{
    private const int MaxConcurrentTransfers = 3;
    private readonly SemaphoreSlim _transferSlots = new(MaxConcurrentTransfers, MaxConcurrentTransfers);

    public async Task<bool> IsDestinationAvailableAsync(
        int catalogId,
        string catalogTypeSlug,
        CancellationToken cancellationToken = default)
    {
        var catalog = await library.GetCatalogAsync(catalogId, cancellationToken);
        return catalog is not null &&
               string.Equals(catalog.CatalogTypeSlug, catalogTypeSlug, StringComparison.OrdinalIgnoreCase) &&
               NormalizeDirectory(catalog.Path) is not null;
    }

    public Task<bool> HasOnlineStreamProviderAsync(MediaKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = registry.ResolveAll<IStreamProviderPlugin>()
            .Where(provider => provider.Mode == StreamProviderMode.Online)
            .Any(provider => provider.Sources.Any(source => source.Kind == kind));
        return Task.FromResult(available);
    }

    public async Task<bool> HasOtherStreamItemActionAsync(
        string pluginId,
        MediaKind kind,
        CancellationToken cancellationToken = default)
    {
        foreach (var plugin in registry.Plugins)
        {
            if (string.Equals(plugin.Descriptor.Id, pluginId, StringComparison.OrdinalIgnoreCase) ||
                plugin is not IPluginActions actions)
                continue;
            var declared = await actions.GetActionsAsync(cancellationToken);
            if (declared.Any(action =>
                    action.Click == PluginActionClick.Dispatch &&
                    action.Scope == PluginActionScope.StreamItem &&
                    action.Kinds?.Contains(kind) == true))
                return true;
        }
        return false;
    }

    public async Task<ResolvedStreamSource?> ResolveStreamAsync(PluginActionContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.ExternalId)) return null;
        foreach (var provider in registry.ResolveAll<IStreamProviderPlugin>())
        {
            var source = !string.IsNullOrWhiteSpace(context.SourceKey)
                ? provider.Sources.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, context.SourceKey, StringComparison.OrdinalIgnoreCase))
                : provider.Mode == StreamProviderMode.Online
                    ? provider.Sources.FirstOrDefault(candidate => context.Kind is null || candidate.Kind == context.Kind)
                    : null;
            if (source is null) continue;
            var result = await provider.ResolveAsync(
                new StreamResolveRequest(source.Key, context.ExternalId, source.Kind, context.Season, context.Episode),
                cancellationToken);
            if (result?.UpstreamUri is { } uri && await StreamingService.IsSafeStreamUri(uri, cancellationToken))
                return new ResolvedStreamSource(uri, source.Kind);
            return null;
        }
        return null;
    }

    public async Task TransferAsync(
        MediaTransferRequest request,
        IProgress<MediaToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CatalogId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "A destination catalog is required.");
        if (!Enum.IsDefined(request.Profile))
            throw new ArgumentOutOfRangeException(nameof(request), "The requested transcode profile is not supported.");
        if (request.Kind == MediaKind.Document)
            throw new InvalidOperationException("Document files cannot be processed by the media transfer tool.");
        await bootstrapState.WaitAsync(cancellationToken);
        if (!await StreamingService.IsSafeStreamUri(request.SourceUri, cancellationToken))
            throw new InvalidOperationException("The media source must be a public HTTPS URL.");

        await _transferSlots.WaitAsync(cancellationToken);
        try
        {
            var catalog = await library.GetCatalogAsync(request.CatalogId, cancellationToken)
                ?? throw new InvalidOperationException("The selected destination catalog no longer exists.");
            var catalogSlug = CatalogTypeSlug(request.Kind);
            if (catalogSlug is null || !string.Equals(catalog.CatalogTypeSlug, catalogSlug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected catalog does not support this media type.");
            var catalogDirectory = NormalizeDirectory(catalog.Path)
                ?? throw new InvalidOperationException("The selected catalog has no writable folder location.");

            var fileExtension = FileExtension(request.Kind, request.Profile);
            var fileStem = SanitizeFileName(request.Title);
            var outputDirectory = catalogDirectory;
            if (request.Kind == MediaKind.Music && request.MusicMetadata is { } music)
            {
                var artist = music.AlbumArtists?.FirstOrDefault()?.Name
                    ?? music.Artists?.FirstOrDefault()?.Name
                    ?? "Unknown Artist";
                var album = music.Album ?? "Singles";
                outputDirectory = Path.GetFullPath(Path.Combine(
                    catalogDirectory,
                    SanitizeFileName(artist),
                    SanitizeFileName(album)));
                EnsureContained(catalogDirectory, outputDirectory);

                if (music.TrackNumber is > 0)
                {
                    var prefix = music.DiscNumber is > 1
                        ? $"{music.DiscNumber.Value:D2}-{music.TrackNumber.Value:D2} - "
                        : $"{music.TrackNumber.Value:D2} - ";
                    fileStem = prefix + fileStem;
                }
            }

            var finalPath = Path.GetFullPath(Path.Combine(outputDirectory, fileStem + fileExtension));
            EnsureContained(catalogDirectory, finalPath);
            Directory.CreateDirectory(outputDirectory);

            var toolPaths = await tools.EnsureInstalledAsync(cancellationToken, progress);
            progress?.Report(new MediaToolProgress(0.1, "Probing source media…"));
            var duration = await ProbeDurationAsync(toolPaths.Ffprobe, request.SourceUri, cancellationToken);
            var scratchDirectory = request.ScratchInCatalog
                ? outputDirectory
                : Path.Combine(toolDirectory.Root, "scratch");
            Directory.CreateDirectory(scratchDirectory);
            var scratchFileName = SafeJobId(request.JobId) + ".partial" + fileExtension;
            var scratchPath = Path.GetFullPath(Path.Combine(scratchDirectory, scratchFileName));
            var libraryItem = await library.SaveAsync(new LibraryItemWriteRequest(
                CatalogItemId: null,
                CatalogTypeId: catalog.CatalogTypeId,
                Kind: request.Kind,
                Title: request.Title,
                ExternalId: request.ExternalId,
                ImageUrl: request.ImageUrl,
                BackdropUrl: request.BackdropUrl,
                Overview: request.Overview,
                Year: request.Year,
                CatalogId: catalog.Id)
            {
                MusicMetadata = request.MusicMetadata,
                SeriesTitle = request.SeriesTitle,
                SeriesExternalId = request.SeriesExternalId,
                SeriesImageUrl = request.SeriesImageUrl,
                SeriesBackdropUrl = request.SeriesBackdropUrl,
                SeriesOverview = request.SeriesOverview,
                SeriesYear = request.SeriesYear,
                SeasonTitle = request.SeasonTitle,
                SeasonNumber = request.SeasonNumber,
                EpisodeNumber = request.EpisodeNumber,
            }, cancellationToken);
            if (libraryItem.ItemId is not int itemId)
                throw new InvalidOperationException("The host could not create the destination library item.");

            try
            {
                progress?.Report(new MediaToolProgress(0.01, "Transcoding media…"));
                await RunTranscodeAsync(toolPaths.Ffmpeg, request, scratchPath, duration, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(scratchPath, finalPath, overwrite: true);
                await library.UpdateStoragePathAsync(itemId, finalPath, cancellationToken);
                progress?.Report(new MediaToolProgress(1, "Media transfer complete."));
            }
            catch
            {
                TryDelete(scratchPath);
                if (libraryItem.Created)
                {
                    TryDelete(finalPath);
                    await library.DeleteAsync(itemId, CancellationToken.None);
                }
                throw;
            }
        }
        finally
        {
            _transferSlots.Release();
        }
    }

    private async Task RunTranscodeAsync(
        string ffmpegPath,
        MediaTransferRequest request,
        string scratchPath,
        long? durationMicros,
        IProgress<MediaToolProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (extension, arguments) = TranscodeArguments(request);
        _ = extension;
        arguments = AddMusicTags(arguments, request);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        FfmpegCertificateTrust.Configure(startInfo, toolDirectory.Root, logger);
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                     "-protocol_whitelist", "https,tls,tcp,crypto,http", "-i", request.SourceUri.ToString(),
                 })
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add(scratchPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The managed FFmpeg tool could not be started.");
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (durationMicros is > 0 && line.StartsWith("out_time_ms=", StringComparison.Ordinal) &&
                long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
            {
                progress?.Report(new MediaToolProgress(
                    0.1 + Math.Clamp(micros / (double)durationMicros.Value, 0, 1) * 0.88,
                    "Transcoding media…"));
            }
        }
        await process.WaitForExitAsync(cancellationToken);
        var error = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"FFmpeg exited with code {process.ExitCode}."
                : error.Trim());
        if (!File.Exists(scratchPath))
            throw new InvalidOperationException("FFmpeg completed without creating the expected output file.");
    }

    private async Task<long?> ProbeDurationAsync(string ffprobePath, Uri sourceUri, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            FfmpegCertificateTrust.Configure(startInfo, toolDirectory.Root, logger);
            foreach (var argument in new[]
                     {
                         "-v", "error", "-protocol_whitelist", "https,tls,tcp,crypto,http",
                         "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1",
                         sourceUri.ToString(),
                     })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            using var registration = cancellationToken.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 &&
                   double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? (long)(seconds * 1_000_000)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not probe media duration; progress will be indeterminate.");
            return null;
        }
    }

    private static (string Extension, string[] Arguments) TranscodeArguments(MediaTransferRequest request)
    {
        if (request.Kind is MediaKind.Music or MediaKind.Podcast or MediaKind.Audiobook or MediaKind.Book)
        {
            return request.Profile switch
            {
                MediaTranscodeProfile.CopyToMp4 or MediaTranscodeProfile.CopyAudio => (".m4a", ["-map", "0:a:0", "-vn", "-c:a", "copy"]),
                MediaTranscodeProfile.Mp3Audio => (".mp3", ["-map", "0:a:0", "-vn", "-c:a", "libmp3lame", "-q:a", "2"]),
                MediaTranscodeProfile.OpusAudio => (".opus", ["-map", "0:a:0", "-vn", "-c:a", "libopus", "-b:a", "160k"]),
                _ => (".m4a", ["-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k"]),
            };
        }

        return request.Profile switch
        {
            MediaTranscodeProfile.H264 => (".mp4", ["-map", "0:v:0", "-map", "0:a?", "-sn", "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
            MediaTranscodeProfile.H265 => (".mp4", ["-map", "0:v:0", "-map", "0:a?", "-sn", "-c:v", "libx265", "-preset", "medium", "-crf", "28", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
            MediaTranscodeProfile.Av1 => (".mp4", ["-map", "0:v:0", "-map", "0:a?", "-sn", "-c:v", "libsvtav1", "-preset", "6", "-crf", "30", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
            _ => (".mp4", ["-map", "0:v:0", "-map", "0:a?", "-sn", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart"]),
        };
    }

    private static string[] AddMusicTags(string[] arguments, MediaTransferRequest request)
    {
        if (request.Kind != MediaKind.Music || request.MusicMetadata is not { } music)
            return arguments;

        var result = arguments.ToList();
        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            result.Add("-metadata");
            result.Add($"{key}={value}");
        }

        var artists = music.Artists ?? Array.Empty<MusicArtistCredit>();
        var albumArtists = music.AlbumArtists ?? Array.Empty<MusicArtistCredit>();
        var albumArtistNames = (albumArtists.Count > 0 ? albumArtists : artists.Take(1).ToArray())
            .Select(credit => credit.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        Add("title", request.Title);
        Add("artist", string.Join("; ", artists.Select(credit => credit.Name).Where(name => !string.IsNullOrWhiteSpace(name))));
        Add("album_artist", albumArtistNames.Length > 0 ? string.Join("; ", albumArtistNames) : "Unknown Artist");
        Add("album", music.Album);
        Add("track", music.TrackNumber is > 0
            ? music.TrackCount is > 0 ? $"{music.TrackNumber}/{music.TrackCount}" : music.TrackNumber.Value.ToString(CultureInfo.InvariantCulture)
            : null);
        Add("disc", music.DiscNumber is > 0
            ? music.DiscCount is > 0 ? $"{music.DiscNumber}/{music.DiscCount}" : music.DiscNumber.Value.ToString(CultureInfo.InvariantCulture)
            : null);
        Add("date", music.Year?.ToString(CultureInfo.InvariantCulture) ?? request.Year?.ToString(CultureInfo.InvariantCulture));
        Add("genre", string.Join("; ", (music.Genres ?? Array.Empty<string>()).Where(genre => !string.IsNullOrWhiteSpace(genre))));
        Add("isrc", music.Isrc);
        Add("musicbrainz_trackid", music.MusicBrainzRecordingId);
        Add("musicbrainz_albumid", music.MusicBrainzReleaseId);
        Add("musicbrainz_artistid", string.Join("; ", artists
            .Select(credit => credit.MusicBrainzId)
            .Where(id => !string.IsNullOrWhiteSpace(id))));
        return result.ToArray();
    }

    private static string? CatalogTypeSlug(MediaKind kind) => kind switch
    {
        MediaKind.Movie => "movies",
        MediaKind.Tv => "tv-shows",
        MediaKind.Music => "music",
        MediaKind.Podcast => "podcasts",
        MediaKind.Audiobook => "audiobooks",
        MediaKind.Book => "books",
        MediaKind.Document => "docs",
        _ => null,
    };

    private static string FileExtension(MediaKind kind, MediaTranscodeProfile profile) =>
        TranscodeArguments(new MediaTransferRequest(new Uri("https://invalid.example"), 1, kind, "", Profile: profile)).Extension;

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void EnsureContained(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The output file path escapes the selected catalog.");
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        var sanitized = value.Trim().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "media" : sanitized;
    }

    private static string SafeJobId(string? jobId) =>
        !string.IsNullOrWhiteSpace(jobId) && jobId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? jobId
            : Guid.NewGuid().ToString("N");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
