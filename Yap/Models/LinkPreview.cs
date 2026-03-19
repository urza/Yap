namespace Yap.Models;

public class LinkPreview
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? SiteName { get; set; }
    public DateTime FetchedAt { get; set; }
    public bool Failed { get; set; }
    public bool HasContent => !Failed && (!string.IsNullOrEmpty(Title) || !string.IsNullOrEmpty(Description));
}
