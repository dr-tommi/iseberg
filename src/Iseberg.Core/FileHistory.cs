using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iseberg.Core;

/// <summary>A saved state of a script file, kept in the version history.</summary>
public sealed record FileVersion(string StoragePath, DateTime SavedUtc, long Size, string Hash);

/// <summary>
/// Keeps copies of local script files after each save, one folder per file, so earlier states can be compared and restored.
/// Each version is stored with its exact bytes; identical consecutive states are stored once.
/// </summary>
public sealed class FileHistory(string? directory = null)
{
    private const string Extension = ".version";
    private const string IndexName = "index.json";
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iseberg", "History");

    public string Directory { get; } = directory ?? DefaultDirectory;

    /// <summary>The most versions kept per file; older versions are removed when a new one is added.</summary>
    public int MaxVersions { get; init; } = 50;

    /// <summary>Versions older than this many days are removed; the newest version is always kept.</summary>
    public int MaxAgeDays { get; init; } = 90;

    /// <summary>The folder holding the versions of a file. Paths differing only by case share a folder on Windows.</summary>
    public string FolderOf(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var key = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        return Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
    }

    /// <summary>The versions of a file, newest first.</summary>
    public IReadOnlyList<FileVersion> List(string filePath)
    {
        var folder = FolderOf(filePath);
        if (!System.IO.Directory.Exists(folder)) return [];
        var versions = new List<FileVersion>();
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in System.IO.Directory.EnumerateFiles(folder, "*" + Extension))
        {
            // Names are <stamp>_<sequence>_<hash>; the sequence orders saves within the same millisecond.
            var parts = Path.GetFileNameWithoutExtension(path).Split('_');
            if (parts.Length != 3 || !DateTime.TryParseExact(parts[0], StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var saved) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                continue;
            versions.Add(new FileVersion(path, saved, new FileInfo(path).Length, parts[2]));
            order[path] = sequence;
        }
        return versions.OrderByDescending(version => version.SavedUtc).ThenByDescending(version => order[version.StoragePath]).ToArray();
    }

    /// <summary>Stores a state of a file unless it equals the newest version. Returns the new version, or null when unchanged.</summary>
    public FileVersion? Add(string filePath, byte[] bytes, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var now = nowUtc ?? DateTime.UtcNow;
        var fullPath = Path.GetFullPath(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = List(fullPath);
        if (existing.Count > 0 && existing[0].Hash == hash) return null;
        var folder = FolderOf(fullPath);
        System.IO.Directory.CreateDirectory(folder);
        WriteIndex(folder, fullPath);
        // Versions saved within the same millisecond get a counter so none replaces another.
        var stamp = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var sequence = System.IO.Directory.EnumerateFiles(folder, $"{stamp}_*{Extension}").Count();
        var target = Path.Combine(folder, $"{stamp}_{sequence:000}_{hash}{Extension}");
        while (File.Exists(target)) target = Path.Combine(folder, $"{stamp}_{++sequence:000}_{hash}{Extension}");
        WriteAtomically(target, bytes);
        Prune(fullPath, now);
        return new FileVersion(target, DateTime.ParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), bytes.LongLength, hash);
    }

    /// <summary>Stores the file's current content from disk, if it exists. Returns the new version, or null.</summary>
    public async Task<FileVersion?> AddFromDiskAsync(string filePath, DateTime? nowUtc = null)
    {
        if (!File.Exists(filePath)) return null;
        var bytes = await File.ReadAllBytesAsync(filePath);
        return Add(filePath, bytes, nowUtc);
    }

    /// <summary>Removes versions beyond <see cref="MaxVersions"/> and versions older than <see cref="MaxAgeDays"/>.</summary>
    public void Prune(string filePath, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var versions = List(filePath);
        for (var i = 1; i < versions.Count; i++)
        {
            var tooMany = MaxVersions > 0 && i >= MaxVersions;
            var tooOld = MaxAgeDays > 0 && now - versions[i].SavedUtc > TimeSpan.FromDays(MaxAgeDays);
            if (tooMany || tooOld) File.Delete(versions[i].StoragePath);
        }
    }

    /// <summary>Deletes every version of a file.</summary>
    public void Clear(string filePath)
    {
        var folder = FolderOf(filePath);
        if (System.IO.Directory.Exists(folder)) System.IO.Directory.Delete(folder, recursive: true);
    }

    public static Task<byte[]> ReadAsync(FileVersion version) => File.ReadAllBytesAsync(version.StoragePath);

    /// <summary>The original file path recorded for a history folder, or null when the index is missing.</summary>
    public static string? OriginalPathOf(string folder)
    {
        var index = Path.Combine(folder, IndexName);
        if (!File.Exists(index)) return null;
        try { return JsonSerializer.Deserialize<HistoryIndex>(File.ReadAllText(index))?.Path; }
        catch (JsonException) { return null; }
    }

    private static void WriteIndex(string folder, string fullPath)
    {
        var index = Path.Combine(folder, IndexName);
        if (OriginalPathOf(folder) == fullPath) return;
        WriteAtomically(index, JsonSerializer.SerializeToUtf8Bytes(new HistoryIndex { Path = fullPath }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class HistoryIndex
    {
        public string Path { get; set; } = "";
    }
}
