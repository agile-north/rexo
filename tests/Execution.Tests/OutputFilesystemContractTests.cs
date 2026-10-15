namespace Rexo.Execution.Tests;

using Rexo.Artifacts;
using Rexo.Configuration.Models;
using Rexo.Core.Models;
using Rexo.Execution;
using Rexo.Templating;
using Rexo.Versioning;

public sealed class OutputFilesystemContractTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(null, true, true)]
    [InlineData(false, true, false)]
    public async Task OutputTreeExistsDuringExecutionOnlyWhenPrecreationIsEnabled(
        bool? emit, bool? precreate, bool expected)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-output-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputs = new RepoOutputsConfig
            {
                Emit = emit,
                PrecreateDirectories = precreate,
                Root = "out",
                Tests = new RepoTestOutputPathsConfig { Results = "custom/results" },
                Security = new RepoSecurityOutputPathsConfig { Audit = "custom/security/audit.json" },
                Manifests = new RepoManifestOutputsConfig { Path = Path.Join(root, "custom", "manifests") },
            };
            var inspected = false;
            var (executor, builtins) = CreateExecutor(root, outputs);
            builtins.Register("builtin:inspect-output-tree", (step, context, ct) =>
            {
                inspected = true;
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "custom", "results")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "custom", "security")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "custom", "manifests")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "tests", "coverage")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "tests", "reports")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "analysis", "sarif")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "security", "sarif")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "packages")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "logs")));
                Assert.Equal(expected, Directory.Exists(Path.Join(root, "out", "tmp")));
                Assert.False(File.Exists(Path.Join(root, "custom", "security", "audit.json")));
                return Task.FromResult(new StepResult(step.Id!, true, 0, TimeSpan.Zero, new Dictionary<string, object?>()));
            });

            var result = await ExecuteAsync(executor, root);

            Assert.True(result.Success);
            Assert.True(inspected);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrecreationCleanupPreservesExistingDirectoriesAndProducedFiles(bool success)
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-output-cleanup-{Guid.NewGuid():N}");
        var existing = Path.Join(root, "out", "logs");
        Directory.CreateDirectory(existing);
        try
        {
            var (executor, builtins) = CreateExecutor(root, new RepoOutputsConfig
            {
                PrecreateDirectories = true,
                Root = "out",
            });
            var report = Path.Join(root, "out", "analysis", "report.txt");
            builtins.Register("builtin:inspect-output-tree", async (step, context, ct) =>
            {
                Assert.True(Directory.Exists(Path.GetDirectoryName(report)));
                await File.WriteAllTextAsync(report, "evidence", ct);
                return new StepResult(step.Id!, success, success ? 0 : 1, TimeSpan.Zero, new Dictionary<string, object?>());
            });

            var result = await ExecuteAsync(executor, root);

            Assert.Equal(success, result.Success);
            Assert.True(Directory.Exists(existing));
            Assert.Equal("evidence", await File.ReadAllTextAsync(report));
            Assert.False(Directory.Exists(Path.Join(root, "out", "tests")));
            Assert.False(Directory.Exists(Path.Join(root, "out", "manifests")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AbsentOutputSettingsDoNotCreateDirectoriesDuringExecution()
    {
        var root = Path.Join(Path.GetTempPath(), $"rexo-output-defaults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inspected = false;
            var (executor, builtins) = CreateExecutor(root, null);
            builtins.Register("builtin:inspect-output-tree", (step, context, ct) =>
            {
                inspected = true;
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                return Task.FromResult(new StepResult(step.Id!, true, 0, TimeSpan.Zero, new Dictionary<string, object?>()));
            });

            Assert.True((await ExecuteAsync(executor, root)).Success);
            Assert.True(inspected);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static (DefaultCommandExecutor Executor, BuiltinRegistry Builtins) CreateExecutor(
        string root, RepoOutputsConfig? outputs)
    {
        var config = new RepoConfig("test", new Dictionary<string, RepoCommandConfig>
        {
            ["inspect"] = new("Inspect filesystem", new Dictionary<string, RepoOptionConfig>(),
                [new RepoStepConfig(Id: "inspect", Uses: "builtin:inspect-output-tree")]),
        }, null) { Outputs = outputs };
        var builtins = new BuiltinRegistry();
        var loader = new ConfigCommandLoader(builtins, new TemplateRenderer(),
            VersionProviderRegistry.CreateDefault(), new ArtifactProviderRegistry());
        var registry = new CommandRegistry();
        var executor = new DefaultCommandExecutor(registry);
        loader.LoadInto(registry, config, root, executor);
        return (executor, builtins);
    }

    private static Task<CommandResult> ExecuteAsync(DefaultCommandExecutor executor, string root) =>
        executor.ExecuteAsync("inspect", new CommandInvocation(
            new Dictionary<string, string>(), new Dictionary<string, string?>(),
            Json: false, JsonFile: null, WorkingDirectory: root), CancellationToken.None);
}
