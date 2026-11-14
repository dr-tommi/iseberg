using System.Text;
using Iseberg.Core;
using Xunit;

namespace Iseberg.Tests;

public sealed class FileHistoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iseberg-history-" + Guid.NewGuid().ToString("N"));

    private string Script => Path.Combine(directory, "scripts", "Test.ps1");

    [Fact]
    public void VersionsAreListedNewestFirstAndIdenticalStatesAreSkipped()
    {
        var history = new FileHistory(Path.Combine(directory, "history"));
        var start = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.Empty(history.List(Script));
        Assert.NotNull(history.Add(Script, "one"u8.ToArray(), start));
        Assert.Null(history.Add(Script, "one"u8.ToArray(), start.AddMinutes(1)));
        var two = history.Add(Script, "two"u8.ToArray(), start.AddMinutes(2))!;
        Assert.NotNull(history.Add(Script, "one"u8.ToArray(), start.AddMinutes(3)));

        var versions = history.List(Script);
        Assert.Equal(3, versions.Count);
        Assert.Equal([start.AddMinutes(3), start.AddMinutes(2), start], versions.Select(version => version.SavedUtc));
        Assert.Equal(two, versions[1]);
        Assert.Equal(3, versions[1].Size);
        Assert.Equal("two", Encoding.UTF8.GetString(File.ReadAllBytes(versions[1].StoragePath)));
        Assert.Equal(Path.GetFullPath(Script), FileHistory.OriginalPathOf(history.FolderOf(Script)));
    }

    [Fact]
    public void SavesWithinTheSameMillisecondAreAllKept()
    {
        var history = new FileHistory(Path.Combine(directory, "history"));
        var now = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        history.Add(Script, "one"u8.ToArray(), now);
        history.Add(Script, "two"u8.ToArray(), now);
        history.Add(Script, "one"u8.ToArray(), now);
        Assert.Equal(3, history.List(Script).Count);
    }

    [Fact]
    public void OldAndSurplusVersionsArePrunedButTheNewestStays()
    {
        var history = new FileHistory(Path.Combine(directory, "history")) { MaxVersions = 3, MaxAgeDays = 30 };
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++) history.Add(Script, Encoding.UTF8.GetBytes("v" + i), start.AddDays(i));
        Assert.Equal(["v4", "v3", "v2"], history.List(Script).Select(version => Encoding.UTF8.GetString(File.ReadAllBytes(version.StoragePath))));

        // Long after the last save only the newest version is left.
        history.Prune(Script, start.AddDays(200));
        Assert.Equal(["v4"], history.List(Script).Select(version => Encoding.UTF8.GetString(File.ReadAllBytes(version.StoragePath))));
    }

    [Fact]
    public void EachFileHasItsOwnHistoryAndCanBeCleared()
    {
        var history = new FileHistory(Path.Combine(directory, "history"));
        var other = Path.Combine(directory, "scripts", "Other.ps1");
        history.Add(Script, "a"u8.ToArray());
        history.Add(other, "b"u8.ToArray());
        if (OperatingSystem.IsWindows()) Assert.Single(history.List(Script.ToUpperInvariant()));
        history.Clear(Script);
        Assert.Empty(history.List(Script));
        Assert.Single(history.List(other));
    }

    [Fact]
    public async Task AddFromDiskKeepsTheExactBytes()
    {
        var history = new FileHistory(Path.Combine(directory, "history"));
        Assert.Null(await history.AddFromDiskAsync(Script));
        Directory.CreateDirectory(Path.GetDirectoryName(Script)!);
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Write-Output 'Grüße'\r\n")];
        await File.WriteAllBytesAsync(Script, bytes, TestContext.Current.CancellationToken);
        var version = await history.AddFromDiskAsync(Script);
        Assert.Equal(bytes, await FileHistory.ReadAsync(version!));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
