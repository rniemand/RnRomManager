using Microsoft.Extensions.Options;
using NLog;
using RnRomManager.Core.Audit;
using RnRomManager.Core.Database;
using RnRomManager.Core.Users;

namespace RnRomManager.Api.Cli;

/// <summary>
/// <c>--reset-password &lt;username&gt;</c> (spec §9.1, D37): startup steps 1, 2 and 5 only (no migrations), then a new
/// random password printed once to stdout. Failures go to stderr with exit 1. Safe while the app is running.
/// </summary>
public static class ResetPasswordCommand
{
    public static async Task<int> RunAsync(string[] args, string username, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            // Step 2 (NLog) runs while building, as in the web host, so a config error is still logged.
            await using var app = AppHost.CreateBuilder(args).Build();
            app.Services.GetRequiredService<IStartupValidator>().Validate();
            await app.Services.GetRequiredService<IDatabaseLifecycle>().WaitUntilAvailableAsync(CancellationToken.None);

            if (await app.Services.GetRequiredService<UserAdminService>().RecoverAsync(username, CancellationToken.None) is not var (user, password, promoted, reenabled))
            {
                await stderr.WriteLineAsync($"User '{username}' not found.");
                return 1;
            }

            await stdout.WriteLineAsync(
                $"New password for user '{user.Username}' (shown once, must be changed at next login, roles {(user.Roles.Count > 0 ? string.Join(", ", user.Roles) : "none")}): {password}");
            await RecordAsync(app.Services, user.Username, promoted, reenabled, stderr);
            return 0;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Password reset failed: {ex.Message}");
            return 1;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    /// <summary>
    /// U9: the CLI host never starts <c>AuditWriter</c>, so the row is inserted synchronously, after the password was
    /// printed; a failure only warns (AU2 best effort) and keeps exit code 0.
    /// </summary>
    private static async Task RecordAsync(IServiceProvider services, string username, bool promoted, bool reenabled, TextWriter stderr)
    {
        try
        {
            var details = $"user={username} via=cli{(promoted ? " promoted=admin" : "")}{(reenabled ? " reenabled=true" : "")}";
            var auditEvent = new AuditEvent(AuditActions.PasswordReset, new AuditActor(null, null, null), Details: details)
            {
                OccurredAt = services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime,
            };
            await services.GetRequiredService<IAuditRepository>().InsertAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Warning: the password was reset, but its audit event wasn't written: {ex.Message}");
        }
    }
}
