namespace RnRomManager.Core.Users;

/// <summary>Outcome of a <see cref="UserAdminService"/> call; the API maps each failure to its §23.1 code.</summary>
public sealed record UserAdminResult
{
    public enum Failure
    {
        None,
        NotFound,
        ValidationFailed,
        UsernameTaken,
        LastAdmin,
    }

    public User? User { get; private init; }
    public Failure Error { get; private init; }
    public Dictionary<string, string[]>? Errors { get; private init; }

    public static UserAdminResult Ok(User user) => new() { User = user };
    public static UserAdminResult Fail(Failure error) => new() { Error = error };
    public static UserAdminResult Invalid(string field, string message) =>
        new() { Error = Failure.ValidationFailed, Errors = new() { [field] = [message] } };
}
