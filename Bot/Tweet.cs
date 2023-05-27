using System.Text.Json.Serialization;

namespace TelegramBot;

public class Tweet
{

    [JsonPropertyName("text")]
    public string Text { get; set; }

    [JsonPropertyName("entities")]
    public Entities? Entities { get; set; }

}

public class Entities
{
    [JsonPropertyName("media")]
    public List<Media?>? Media { get; set; }
}


public class Media
{
    [JsonPropertyName("media_url_https")]
    public string? MediaUrlHttps { get; set; }

    [JsonPropertyName("expanded_url")]
    public string? ExpandedUrl { get; set; }
}