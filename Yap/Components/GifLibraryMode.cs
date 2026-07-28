namespace Yap.Components;

/// <summary>Which library a GifLibraryManager instance manages.</summary>
public enum GifLibraryMode
{
    /// <summary>The current user's pool: starred favorites + their own imports. Shown in Settings.</summary>
    User,

    /// <summary>The admin-curated server library visible to everyone. Shown in the Admin panel.</summary>
    Server,
}
