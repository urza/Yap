namespace Yap.Models;

public class Room
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public string? CreatedBy { get; init; }
    public bool IsDefault { get; init; }

    public Room(string name, string? createdBy = null, bool isDefault = false)
    {
        Name = name;
        CreatedBy = createdBy;
        IsDefault = isDefault;
    }
}
