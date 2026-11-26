namespace RnRomManager.Core.Users;

public enum UserAdminResultFailure
{
    None,
    NotFound,
    ValidationFailed,
    UsernameTaken,
    LastAdmin,
    SelfChange,
}
