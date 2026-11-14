using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

[Collection(PowerShellPolicyCollection.Name)]
public sealed class ExecutionPolicyTests
{
    [Fact]
    public async Task SavedLocalScriptsRunWithoutEnablingThemFirst()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "iseberg-policy-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "saved.ps1");
        var previousPolicy = Environment.GetEnvironmentVariable("PSExecutionPolicyPreference");
        try
        {
            // Other tests unlock scripts for the process; this one must see only the shipped configuration.
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", null);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "Write-Output 'saved script ran'", TestContext.Current.CancellationToken);
            await using var session = new PowerShellSession();
            var output = new System.Collections.Concurrent.ConcurrentQueue<OutputEntry>();
            session.Output += output.Enqueue;
            await session.InitializeAsync();
            await session.ExecuteAsync("(Get-ExecutionPolicy -Scope LocalMachine).ToString()");
            await session.ExecuteAsync("", path);
            var text = string.Concat(output.Where(entry => entry.Kind != OutputKind.Command).Select(entry => entry.Text));
            Assert.Contains("RemoteSigned", text);
            Assert.Contains("saved script ran", text);
            Assert.DoesNotContain(output, entry => entry.Kind == OutputKind.Error);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSExecutionPolicyPreference", previousPolicy);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
