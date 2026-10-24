namespace Rexo.Configuration;

using Rexo.Configuration.Models;

public static class CommandUserResolver
{
    public static RepoStepConfig ApplyDefaults(RepoStepConfig step, RepoCommandConfig command)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(command);
        if (step.Run is null || step.HostUserDefaultsApplied)
        {
            return step;
        }

        // An explicit account override must not borrow another account's credentials.
        var inheritsCredentials = step.User is null ||
            string.Equals(step.User, command.User, StringComparison.Ordinal);
        return step with
        {
            User = step.User ?? command.User,
            Domain = step.Domain ?? (inheritsCredentials ? command.Domain : null),
            PasswordSecret = step.PasswordSecret ?? (inheritsCredentials ? command.PasswordSecret : null),
            HostUserDefaultsApplied = true,
        };
    }
}
