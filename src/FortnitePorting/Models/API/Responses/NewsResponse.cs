using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

namespace FortnitePorting.Models.API.Responses;

public class NewsResponse
{
    [JsonProperty("entries")] public NewsEntry[] Entries { get; set; }
}

public class NewsEntry : ObservableObject
{
    private string? _image;

    [JsonProperty("title")] public string Title { get; set; }
    [JsonProperty("subtitle")] public string SubTitle { get; set; }
    [JsonProperty("tag")] public string Tag { get; set; }
    [JsonProperty("description")] public string Description { get; set; }
    [JsonProperty("imageUrl")] public string? Image { get => _image; set => SetProperty(ref _image, value); }
    [JsonProperty("date")] public DateTime Date { get; set; }
}