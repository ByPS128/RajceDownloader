using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using RajceDownloader.Main.Json;

namespace RajceDownloader.Main;

public static class Program
{
    private static readonly JsonSerializerOptions _jsonOptions = new ()
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

        var sw = Stopwatch.StartNew();
        try
        {
            Console.WriteLine("Starting RajceDownloader.Main instance.");

            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            _invalidCharsRegex = new Regex($"[{Regex.Escape(regexSearch)}]");

            _configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false)
                .Build();

            AppOptions appOptions = new ();
            _configuration.Bind(nameof(AppOptions), appOptions);

            foreach (string album in appOptions.Albums ?? Array.Empty<string>())
            {
                await DownloadAlbumAsync(new Uri(album), appOptions.SkipExistingFiles, appOptions.MaxDegreeOfParallelDownload, cancellationToken);
            }

            foreach (string video in appOptions.Videos ?? Array.Empty<string>())
            {
                await DownloadVideoAsync(new Uri(video), appOptions.SkipExistingFiles, appOptions.MaxDegreeOfParallelDownload, cancellationToken);
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

        sw.Stop();
        Console.WriteLine($"\n\nRajceDownloader.Main finished in {sw.Elapsed.TotalSeconds:0.00} seconds.");

        return 0;
    }

    private static async Task DownloadAlbumAsync(Uri albumUri, bool skipExistingFiles, int maxDegreeOfParallelDownload, CancellationToken cancellationToken)
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

        await DownloadAlbumInternalAsync(albumName, albumUri, outputDirectory, skipExistingFiles, maxDegreeOfParallelDownload, cancellationToken);
    }

    private static async Task DownloadAlbumInternalAsync(string albumName, Uri albumUri, string outputDirectory, bool skipExistingFiles, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        Console.WriteLine($"\n--[ Album: {albumName} ]-------------------\nDownloading url: {albumUri}");

        using HttpClient client = new ();
        string content = await client.GetStringAsync(albumUri, cancellationToken);

        string storageUrl = ExtractAndCleanJsVariable(content, "var storage = ");
        string photosJson = "[" + ExtractAndCleanJsVariable(content, "var photos = ") + "]";

        var photos = JsonSerializer.Deserialize<List<Photo>>(photosJson, _jsonOptions);
        if (photos == null || !photos.Any())
        {
            Console.WriteLine("No photos.");
            return;
        }

        // Use SemaphoreSlim for limiting max degree of parallelism
        maxDegreeOfParallelism = maxDegreeOfParallelism < 1 ? 1 : maxDegreeOfParallelism;
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        Task[] tasks = photos.Select(async photo =>
        {
            await semaphore.WaitAsync(cancellationToken);

            try
            {
                var fileUri = new Uri($"{storageUrl}images/{photo.FileName}?ver={photo.Version}");
                string outputFilename = Path.Combine(outputDirectory, photo.FileName);
                string extension = Path.GetExtension(photo.FileName);
                bool extensionKnown = !string.IsNullOrEmpty(extension);

                if (skipExistingFiles && File.Exists(outputFilename))
                {
                    Console.Write("-");
                    return; // Skip this file if it already exists
                }

                if (!extensionKnown)
                {
                    byte[] headerBytes = await DownloadFileHeader(client, fileUri, cancellationToken);
                    extension = FileTypeRecognizer.GetFileExtension(headerBytes.AsSpan(0, Math.Min(headerBytes.Length, 12))) ?? extension;
                    outputFilename = Path.ChangeExtension(outputFilename, extension);

                    if (skipExistingFiles && File.Exists(outputFilename))
                    {
                        Console.Write("-");
                        return; // Skip this file if it already exists with the determined extension
                    }

                    await DownloadFileInChunks(client, fileUri, outputFilename, headerBytes, cancellationToken);
                }
                else
                {
                    await DownloadFileInChunks(client, fileUri, outputFilename, null, cancellationToken);
                }

                Console.Write("*");
                SetFileMetadata(outputFilename, photo.Date);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        Console.WriteLine($"\nAll files are stored in directory: '{outputDirectory}'");
    }


    private static async Task DownloadVideoAsync(Uri albumUri, bool skipExistingFiles, int maxDegreeOfParallelDownload, CancellationToken cancellationToken)
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

        await DownloadVideoInternalAsync(albumUri, outputDirectory, skipExistingFiles, maxDegreeOfParallelDownload, cancellationToken);
    }

    private static async Task DownloadVideoInternalAsync(Uri videoUri, string outputDirectory, bool skipExistingFiles, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        Console.WriteLine("\n--[ Video ]-------------------");
        Console.WriteLine($"Downloading url: {videoUri}");

        using HttpClient client = new ();
        string content = await client.GetStringAsync(videoUri, cancellationToken);

        string settingsJson = ExtractAndCleanJsVariable(content, "var settings = ");
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            throw new ApplicationException("Page content does not contain awaited string literals.");
        }

        var settings = JsonSerializer.Deserialize<VideoSettings>("{" + settingsJson + "}", _jsonOptions);
        if (settings?.VideoStructure?.Items == null || settings.VideoStructure.Items.Count < 1)
        {
            Console.WriteLine("No video.");
            return;
        }

        // Prepare for parallel download
        maxDegreeOfParallelism = maxDegreeOfParallelism < 1 ? 1 : maxDegreeOfParallelism;
        using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

        Task[] downloadTasks = settings.VideoStructure.Items
            .Where(i => i.Video != null)
            .SelectMany(item => item.Video.Select((video, index) => new {item.Type, video, index}))
            .Select(async videoInfo =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (videoInfo.video == null)
                    {
                        return;
                    }

                    string validVideoName = MakeValidFileName(settings.VideoName);
                    string outputFilename = Path.Combine(outputDirectory, $"{validVideoName}.{videoInfo.index + 1:00}.{videoInfo.video.Format}");

                    if (ShouldSkipFile(skipExistingFiles, outputFilename, "." + videoInfo.video.Format, out _))
                    {
                        Console.WriteLine($" - Skipping existing {videoInfo.Type}: {settings.VideoName}");
                        return;
                    }

                    Console.WriteLine($" * Downloading {videoInfo.Type}: {settings.VideoName}");
                    var fileUri = new Uri(videoInfo.video.File);

                    // Download the video in chunks
                    await DownloadFileInChunks(client, fileUri, outputFilename, null, cancellationToken);

                    // Set metadata, assuming default date as DateTime.Now for videos.
                    SetFileMetadata(outputFilename, DateTime.Now);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Error downloading {videoInfo.Type}: {settings.VideoName} - {e.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

        // Wait for all download tasks to complete
        await Task.WhenAll(downloadTasks);

        Console.WriteLine($"All files are stored in directory: '{outputDirectory}'");
    }

    private static async Task<byte[]> DownloadFileHeader(HttpClient client, Uri fileUri, CancellationToken cancellationToken, int headerSize = 12)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, fileUri);
        request.Headers.Range = new RangeHeaderValue(0, headerSize - 1);
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task DownloadFileInChunks(HttpClient client, Uri fileUri, string outputFile, byte[] initialData, CancellationToken cancellationToken, int chunkSize = 8192)
    {
        var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, chunkSize, true);
        try
        {
            if (initialData != null)
            {
                await outputStream.WriteAsync(initialData, 0, initialData.Length, cancellationToken);
            }

            var request = new HttpRequestMessage(HttpMethod.Get, fileUri);
            if (initialData != null)
            {
                request.Headers.Range = new RangeHeaderValue(initialData.Length, null);
            }

            using (HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(outputStream);
            }
        }
        finally
        {
            await outputStream.DisposeAsync();
        }
    }

    private static void SetFileMetadata(string filePath, DateTime date)
    {
        File.SetCreationTime(filePath, date);
        File.SetLastWriteTime(filePath, date);
        File.SetLastAccessTime(filePath, date);
    }

    private static string ExtractAndCleanJsVariable(string content, string variableName)
    {
        string line = content.Split("\n").FirstOrDefault(l => l.Trim().StartsWith(variableName))?.Trim()?.Replace(variableName, "");
        line = Regex.Unescape(line);
        return line[1..^2];
    }

    private static bool ShouldSkipFile(bool skipExistingFiles, string outputFilename, string extension, out bool extensionKnown)
    {
        extensionKnown = !string.IsNullOrEmpty(extension);
        return skipExistingFiles && File.Exists(outputFilename) && extensionKnown;
    }

    private static string MakeValidFileName(string name)
    {
        string invalidChars = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
        var regex = new Regex($"[{Regex.Escape(invalidChars)}]");
        return regex.Replace(name, "").Replace(" ", "_");
    }
}