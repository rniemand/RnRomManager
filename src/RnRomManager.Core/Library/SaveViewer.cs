namespace RnRomManager.Core.Library;

/// <summary>User Management S3: who is asking about saves. An Admin sees and acts on every save; a Site user only on their own.</summary>
/// <param name="Username">The session's username; null never owns a save.</param>
public sealed record SaveViewer(string? Username, bool IsAdmin)
{
    public bool CanSee(GameSave save) => IsAdmin || GameSaves.IsOwner(save, Username);
}
