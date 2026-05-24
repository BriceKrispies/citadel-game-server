using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameServer.Persistence;

/// <summary>
/// File-backed durable snapshot store. Persists each key's latest snapshot under a directory so
/// a new instance pointed at the same directory (i.e. after a restart) reads what an earlier
/// instance wrote. Key and snapshot are serialized as JSON; the on-disk file name is a stable
/// hash of the serialized key, so any key type works and last-write-wins per key.
/// </summary>
public sealed class FileSnapshotStore<TKey, TSnapshot> : IDurableSnapshotStore<TKey, TSnapshot>
    where TKey : notnull
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _directory;
    private readonly object _lock = new();

    public FileSnapshotStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A storage directory is required.", nameof(directory));
        }

        _directory = directory;
    }

    public void Save(TKey key, TSnapshot snapshot)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            // Write to a temp file then move, so a reader (or a crash) never sees a partial file.
            var path = PathFor(key);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    public bool TryGetLatest(TKey key, out TSnapshot snapshot)
    {
        lock (_lock)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                snapshot = default!;
                return false;
            }

            var json = File.ReadAllText(path);
            snapshot = JsonSerializer.Deserialize<TSnapshot>(json, JsonOptions)!;
            return true;
        }
    }

    private string PathFor(TKey key)
    {
        var keyJson = JsonSerializer.Serialize(key, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyJson)));
        return Path.Combine(_directory, hash + ".snapshot.json");
    }
}
