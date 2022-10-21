using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RajceDownloader.Main;

public sealed class AppOptions
{
    public string[] Albums { get; init; }
    public string[] Videos { get; init; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Photo
{
    [JsonPropertyName("fileName")] // Because of doubled property with the same name in order to case sensitive access
    public string FileName { get; init; }

    public DateTime Date { get; init; }

    public string Type { get; init; }

    public int Version { get; init; }
}

public sealed class VideoSettings
{
    public string VideoName { get; init; }
    public VideoStructure VideoStructure { get; init; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class VideoStructure
{
    public List<VideoStructureItem> Items { get; init; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class VideoStructureItem
{
    public string Type { get; init; }
    public List<Video> Video { get; init; }
}

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Video
{
    public string File { get; init; }
    public string Type { get; init; }
    public string Format { get; init; }
}