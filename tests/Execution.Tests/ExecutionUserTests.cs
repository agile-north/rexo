namespace Rexo.Execution.Tests;

using System.Text.Json;
using Rexo.Artifacts;
using Rexo.Configuration.Models;
using Rexo.Core.Abstractions;
using Rexo.Core.Models;
using Rexo.Execution.Secrets;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class ExecutionUserTests
{
    [Fact]
    public void WindowsStartInfoKeepsPasswordOutOfArgumentsAndEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string password = "synthetic-login-password";
        var info = ShellRunner.CreateShellStartInfo("echo hello", Path.GetTempPath(), "builder", ".", password,
            new Dictionary<string, string?> { ["REXO_USER_TEST"] = "preserved" });
        Assert.Equal("builder", info.UserName);
        Assert.Equal(".", info.Domain);
        Assert.Equal(password, info.PasswordInClearText);
        Assert.False(info.UseCredentialsForNetworkingOnly);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(Path.Join(Environment.SystemDirectory, "cmd.exe"), info.FileName);
        Assert.Equal("preserved", info.Environment["REXO_USER_TEST"]);
        Assert.DoesNotContain(password, info.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(password, info.Environment.Values);
    }

    [Fact]
    public async Task NativeWithoutUserPreservesOutputAndEnvironment()
    {
        var command = OperatingSystem.IsWindows() ? "echo %REXO_USER_TEST%" : "printf '%s' \"$REXO_USER_TEST\"";
        var result = await ShellRunner.RunAsync(command, Path.GetTempPath(),
            environment: new Dictionary<string, string?> { ["REXO_USER_TEST"] = "preserved" },
            cancellationToken: CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("preserved", result.Stdout);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{{options.missing}}")]
    public async Task InvalidRenderedHostUserFailsWithoutRunning(string user)
    {
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(RunStep() with { User = user }, EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("user must not be empty", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Outputs["stdout"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BlankContainerUserFailsBeforeDockerLaunch(string user)
    {
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(
            RunStep() with { Container = new StepContainerDefinition("unused-image", User: user) },
            EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Container user must not be empty", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownHostAccountNeverRunsAsCurrentUser()
    {
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(RunStep() with { User = MissingAccount() }, EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("Host user execution failed", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Outputs["stdout"]);
    }

    [Fact]
    public async Task HostCredentialsWithoutUserFail()
    {
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(RunStep() with { PasswordSecret = "login" }, EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("require a user", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnixRejectsWindowsCredentialsWithoutLaunching()
    {
        if (OperatingSystem.IsWindows()) return;
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(RunStep() with
        {
            User = Environment.UserName,
            Domain = ".",
            PasswordSecret = "login",
        }, EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("only on Windows", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnixRootCanLaunchChildWithDifferentUidAndGid()
    {
        if (OperatingSystem.IsWindows()) return;
        var current = await ShellRunner.RunAsync("id -u", Path.GetTempPath(), cancellationToken: CancellationToken.None);
        if (current.Stdout != "0") return;
        var expected = await ShellRunner.RunAsync("id -u nobody; id -g nobody", Path.GetTempPath(), cancellationToken: CancellationToken.None);
        if (expected.ExitCode != 0) return;
        using var executor = CreateExecutor();
        var result = await executor.ExecuteAsync(
            RunStep() with { Run = "id -u; id -g", User = "{{options.account}}" },
            EmptyContext() with { Options = new Dictionary<string, string?> { ["account"] = "nobody" } },
            CancellationToken.None);
        Assert.True(result.Success, result.Outputs["stderr"]?.ToString());
        Assert.Equal(expected.Stdout, result.Outputs["stdout"]);
    }

    [Fact]
    public async Task NonTemplateVisibleOptionalSecretCanBeConsumedDirectly()
    {
        var provider = new LoginProvider();
        var providers = new SecretProviderRegistry();
        providers.Register(provider);
        var config = ConfigWithLogin(provider.Type);
        var resolver = new ConfigSecretResolver(config, new Dictionary<string, string>(), providers);
        var preflight = await resolver.PreflightRequiredAsync(CancellationToken.None);
        Assert.True(preflight.Success);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(resolver.ResolvedValues);
        Assert.Equal(LoginProvider.Password, await resolver.GetSecretValueAsync("login", CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        Assert.Empty(resolver.ResolvedValues);
        Assert.Empty(resolver.MappedEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsHostResolvesHiddenOptionalPasswordThroughConfiguredProvider(bool parallel)
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = new LoginProvider();
        var providers = new SecretProviderRegistry();
        providers.Register(provider);
        using var repo = new TempRepo();
        var config = ConfigWithLogin(provider.Type) with
        {
            Commands = new()
            {
                ["run"] = new(null, [], [new RepoStepConfig(Id: "login", Run: "echo must-not-run", Parallel: parallel)])
                {
                    User = MissingAccount(),
                    Domain = ".",
                    PasswordSecret = "{{options.passwordName}}",
                },
            },
        };
        if (parallel)
        {
            config.Commands["run"].Steps.Add(new RepoStepConfig(Id: "login2", Run: "echo must-not-run", Parallel: true));
        }

        var result = await ExecuteAsync(config, repo.Root, providers);
        Assert.False(result.Success);
        Assert.Equal(parallel ? 2 : 1, provider.Calls);
        Assert.Equal(parallel ? 2 : 1, result.Steps.Count);
        foreach (var step in result.Steps)
        {
            Assert.Contains("Host user execution failed", step.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("undefined, empty", step.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, step.Outputs["stdout"]);
        }
        Assert.DoesNotContain(LoginProvider.Password, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(repo.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(LoginProvider.Password, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WindowsMissingPasswordSecretFailsBeforeLogon()
    {
        if (!OperatingSystem.IsWindows()) return;
        var resolver = new ConfigSecretResolver(new RepoConfig("empty", [], []), new Dictionary<string, string>(), new SecretProviderRegistry());
        using var executor = CreateExecutor(resolver);
        var result = await executor.ExecuteAsync(
            RunStep() with { User = MissingAccount(), Domain = ".", PasswordSecret = "missing" },
            EmptyContext(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("undefined, empty, or could not be resolved", result.Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParentUserDoesNotLeakIntoDelegatedCommand()
    {
        using var repo = new TempRepo();
        var config = new RepoConfig("delegation", new()
        {
            ["run"] = new(null, [], [new RepoStepConfig(Command: "child")]) { User = MissingAccount() },
            ["child"] = new(null, [], [new RepoStepConfig(Run: "echo delegated-success")]),
        }, []);
        var result = await ExecuteAsync(config, repo.Root);
        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandUserAppliesToHooks(bool after)
    {
        using var repo = new TempRepo();
        var hook = new List<RepoStepConfig> { new(Id: "hook", Run: "echo must-not-run") };
        var command = new RepoCommandConfig(null, [], [])
        {
            User = MissingAccount(),
            Before = after ? null : hook,
            After = after ? hook : null,
        };
        var result = await ExecuteAsync(new RepoConfig("hooks", new() { ["run"] = command }, []), repo.Root);
        Assert.False(result.Success);
        Assert.Equal("hook", Assert.Single(result.Steps).StepId);
        Assert.Contains("Host user execution failed", result.Steps[0].Outputs["stderr"]?.ToString(), StringComparison.Ordinal);
    }

    private static StepExecutor CreateExecutor(ISecretResolver? resolver = null) =>
        new(new DefaultCommandExecutor(new CommandRegistry()), new TemplateRenderer(), new BuiltinRegistry(), resolver);

    private static ExecutionContext EmptyContext() => ExecutionContext.Empty(Path.GetTempPath());

    private static StepDefinition RunStep() => new("identity", "echo must-not-run", null, null, null);

    private static string MissingAccount() => "rx" + Guid.NewGuid().ToString("N")[..12];

    private static RepoConfig ConfigWithLogin(string provider) => new("login", [], [])
    {
        Secrets = new RepoSecretsConfig
        {
            Items = new()
            {
                ["login"] = new RepoSecretConfig { Provider = provider, Required = false, ExposeInTemplates = false },
            },
        },
    };

    private static async Task<CommandResult> ExecuteAsync(RepoConfig config, string root, SecretProviderRegistry? providers = null)
    {
        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        var loader = new ConfigCommandLoader(new BuiltinRegistry(), new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry(), providers);
        loader.LoadInto(registry, config, root, executor);
        return await executor.ExecuteAsync("run", new CommandInvocation(
            new Dictionary<string, string>(),
            new Dictionary<string, string?> { ["passwordName"] = "login" },
            false, null, root), CancellationToken.None);
    }

    private sealed class LoginProvider : ISecretProvider
    {
        private int _active;
        public const string Password = "synthetic-login-password-not-a-real-credential";
        public string Type => "test-login";
        public int Calls { get; private set; }

        public async Task<SecretResolution> ResolveAsync(SecretRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(1, Interlocked.Increment(ref _active));
            try
            {
                await Task.Delay(10, cancellationToken);
                Calls++;
                return new SecretResolution(request.Name, true, Password, Type, "test");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class TempRepo : IDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), $"rexo-user-execution-{Guid.NewGuid():N}");
        public TempRepo() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
