namespace Yap.Models;

public class MediaUploadLog
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FileType { get; set; } = ""; // "image" or "video"
    public string Extension { get; set; } = "";
    public long? CompressDurationMs { get; set; }
}
