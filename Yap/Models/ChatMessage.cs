namespace Yap.Models;

public record ChatMessage(
    string Username,
    string Content,
    DateTime Timestamp,
    bool IsImage = false
);
