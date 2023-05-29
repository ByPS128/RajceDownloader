using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RajceDownloader.Main.Json;

namespace RajceDownloader.Main;

public static class Program
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = new SnakeCaseJsonNamingPolicy(),
        Converters =
        {
            new RajceDateTimeConverter()
        }
    };

    private static Regex _invalidCharsRegex;
    private static IConfiguration _configuration;

    public static async Task<int> Main()
    {
        var cts = new CancellationTokenSource();
        CancellationToken cancellationToken = cts.Token;
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("\n\nCTRL+C pressed, canceling...");
            cts.Cancel();
            e.Cancel = true;
        };

        try
        {
            Console.WriteLine("Starting RajceDownloader.Main instance.");

            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            _invalidCharsRegex = new Regex($"[{Regex.Escape(regexSearch)}]");

            _configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false)
                .Build();

            AppOptions appOptions = new();
            _configuration.Bind(nameof(AppOptions), appOptions);

            foreach (string album in appOptions.Albums ?? Array.Empty<string>())
            {
                await DownloadAlbumAsync(new Uri(album), cancellationToken);
            }

            foreach (string video in appOptions.Videos ?? Array.Empty<string>())
            {
                await DownloadVideoAsync(new Uri(video), cancellationToken);
            }
        }
        catch (TaskCanceledException e)
        {
            // Cancellation is correct.
        }
        catch (Exception error)
        {
            Console.WriteLine("\n\nRajceDownloader.Main terminated unexpectedly.");
            while (error is not null)
            {
                Console.WriteLine($" > ({error.GetType().Name}) {error.Message}");
                error = error.InnerException;
            }

            return 1;
        }
        finally
        {
            Console.WriteLine("\n\nRajceDownloader.Main finished.");
        }

        return 0;
    }

    private static async Task DownloadAlbumAsync(Uri albumUri, CancellationToken cancellationToken)
    {
        string[] pathParts = albumUri.AbsolutePath.Split("/", StringSplitOptions.RemoveEmptyEntries);

        // Extract album name
        string albumName = pathParts.Last();

        // Extract album owner
        string albumOwner = albumUri.Host.Replace(".rajce.idnes.cz", "", StringComparison.OrdinalIgnoreCase);
        albumOwner = _invalidCharsRegex.Replace(albumOwner, "");
        if (string.Equals(albumOwner, "www", StringComparison.OrdinalIgnoreCase))
        {
            albumOwner = string.Empty;
        }

        // Compose album root path
        string outputDirectory = Path.Combine(Path.GetTempPath(), "Rajce", albumOwner);
        foreach (string dir in pathParts)
        {
            outputDirectory = Path.Combine(outputDirectory, _invalidCharsRegex.Replace(dir, ""));
        }

        if (Directory.Exists(outputDirectory) is false)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await DownloadAlbumInternalAsync(albumName, albumUri, outputDirectory, cancellationToken);
    }

    private static async Task DownloadVideoAsync(Uri albumUri, CancellationToken cancellationToken)
    {
        // Extract album owner
        string albumOwner = albumUri.Host.Replace(".rajce.idnes.cz", "", StringComparison.OrdinalIgnoreCase);
        albumOwner = _invalidCharsRegex.Replace(albumOwner, "");
        if (string.Equals(albumOwner, "www", StringComparison.OrdinalIgnoreCase))
        {
            albumOwner = string.Empty;
        }

        // Compose video root path
        string outputDirectory = Path.Combine(Path.GetTempPath(), "Rajce", albumOwner, "Video");

        if (Directory.Exists(outputDirectory) is false)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await DownloadVideoInternalAsync(albumUri, outputDirectory, cancellationToken);
    }

    private static async Task DownloadAlbumInternalAsync(string albumName, Uri albumUri, string outputDirectory, CancellationToken cancellationToken)
    {
        Console.WriteLine($"\n--[ Album: {albumName} ]-------------------\nDownloading url: {albumUri}");

        // Read the page content
        using HttpClient client = new();
        string content = await client.GetStringAsync(albumUri, cancellationToken);

        // Extract javascript variables.
        string[] lines = content.Split("\n");
        string storageUrl = lines.FirstOrDefault(l => l.Trim().StartsWith("var storage = "))?.Trim()?.Replace("var storage = ", "");
        if (string.IsNullOrWhiteSpace(storageUrl))
        {
            throw new ApplicationException("Page content does not contain awaited string literals.");
        }

        string photosJson = lines.FirstOrDefault(l => l.Trim().StartsWith("var photos = "))?.Trim()?.Replace("var photos = ", "");
        if (string.IsNullOrWhiteSpace(photosJson))
        {
            throw new ApplicationException("Page content does not contain awaited string literals.");
        }

        // Clean them a little bit.
        storageUrl = Regex.Unescape(storageUrl);
        //storageUrl = storageUrl.Substring(1, storageUrl.Length - 3);
        storageUrl = storageUrl[1..^2];
        photosJson = Regex.Unescape(photosJson);
        photosJson = photosJson[..^1];

        // Convert string json to objects
        var photos = JsonSerializer.Deserialize<List<Photo>>(photosJson, _jsonOptions);

        if (photos is null || photos.Count is 0)
        {
            Console.WriteLine("No photos.");
            return;
        }

        // Download and store all the photos / videos
        foreach (Photo photo in photos)
        {
            // https://img25.rajce.idnes.cz/d2503/12/12495/12495718_26b6d575710f209ef4ddfa693c5e9016/images/20160326_182238.jpg?ver=0
            //Console.WriteLine($"Downloading {photo.Type}: {photo.FileName}");
            Console.Write("*");
            var fileUri = new Uri($"{storageUrl}images/{photo.FileName}?ver={photo.Version}");
            try
            {
                byte[] fileBytes = await client.GetByteArrayAsync(fileUri, cancellationToken);
                string outputFilename = Path.Combine(outputDirectory, photo.FileName);
                await File.WriteAllBytesAsync(outputFilename, fileBytes, cancellationToken);
                File.SetCreationTime(outputFilename, photo.Date);
                File.SetLastWriteTime(outputFilename, photo.Date);
                File.SetLastAccessTime(outputFilename, photo.Date);
            }
            catch (HttpRequestException e)
            {
                Console.WriteLine($"\n Error ({e.GetType().Name}) {e.Message}\n URL: {fileUri}");
            }
        }

        Console.WriteLine($"\nAll files are stored in directory: '{outputDirectory}'");
    }

    private static async Task DownloadVideoInternalAsync(Uri videoUri, string outputDirectory, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n--[ Video ]-------------------");
        Console.WriteLine($"Downloading url: {videoUri}");

        // Read the page content
        using HttpClient client = new();
        string content = await client.GetStringAsync(videoUri, cancellationToken);

        // Extract javascript variables.
        string[] lines = content.Split("\n");

        string settingsJson = lines.FirstOrDefault(l => l.Trim().StartsWith("var settings = "))?.Trim()?.Replace("var settings = ", "");
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            throw new ApplicationException("Page content does not contain awaited string literals.");
        }

        // Clean them a little bit.
        settingsJson = Regex.Unescape(settingsJson);
        settingsJson = settingsJson.Substring(0, settingsJson.Length - 1);

        // Convert string json to objects
        var settings = JsonSerializer.Deserialize<VideoSettings>(settingsJson, _jsonOptions);

        if (settings?.VideoStructure?.Items is null || settings.VideoStructure.Items.Count < 1)
        {
            Console.WriteLine("No video.");
            return;
        }

        // Download and store all the photos / videos
        foreach (VideoStructureItem item in settings.VideoStructure.Items.Where(i => i.Video is not null))
        {
            // https://img33.rajce.idnes.cz/d3303/10/10766/10766865_f5ade3378601d29edd245de897dde23e/video/870332360
            for (var v = 0; v < item.Video.Count; v++)
            {
                Video video = item.Video[v];
                if (video is null)
                {
                    continue;
                }

                Console.WriteLine($" * Downloading {item.Type}: {settings.VideoName}");
                var fileUri = new Uri(video.File);
                byte[] fileBytes = await client.GetByteArrayAsync(fileUri, cancellationToken);

                string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
                var r = new Regex($"[{Regex.Escape(regexSearch)}]");

                string validVideoName = r.Replace(settings.VideoName, "").Replace(" ", "_");

                string outputFilename = Path.Combine(outputDirectory, $"{validVideoName}.{v + 1:00}.{video.Format}");
                await File.WriteAllBytesAsync(outputFilename, fileBytes, cancellationToken);
            }
        }

        Console.WriteLine($"All files are stored in directory: '{outputDirectory}'");
    }
}