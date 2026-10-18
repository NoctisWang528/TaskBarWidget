using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

if (args.Length == 2 && args[0] == "--self-test")
{
    var directory = Path.GetFullPath(args[1]);
    if (Directory.Exists(directory)) throw new IOException("Self-test requires a new fixture directory.");
    Directory.CreateDirectory(directory);
    var original = Path.Combine(directory, "source.db");
    var copied = Path.Combine(directory, "snapshot.db");
    using var writer = new SqliteConnection($"Data Source={original};Pooling=False");
    writer.Open();
    using var command = writer.CreateCommand();
    command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE records(value INTEGER); INSERT INTO records VALUES(42);";
    command.ExecuteNonQuery();
    var mainHash = HashFile(original);
    var walHash = HashFile(original + "-wal");
    Snapshot(original, copied);
    using var readback = new SqliteConnection($"Data Source={copied};Mode=ReadOnly;Pooling=False");
    readback.Open();
    using var query = readback.CreateCommand();
    query.CommandText = "SELECT value FROM records";
    if (Convert.ToInt32(query.ExecuteScalar()) != 42 ||
        !mainHash.SequenceEqual(HashFile(original)) ||
        !walHash.SequenceEqual(HashFile(original + "-wal")))
        throw new InvalidDataException("WAL snapshot lost committed data or changed the source.");
    Console.WriteLine("SQLite WAL snapshot and unchanged source verified.");
    return;
}
if (args.Length != 2) throw new ArgumentException("Expected source database and new snapshot path.");
Snapshot(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
Console.WriteLine("SQLite snapshot verified.");

static void Snapshot(string sourcePath, string destinationPath)
{
    if (!File.Exists(sourcePath) || File.Exists(destinationPath) || sourcePath.Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Source must exist and destination must be new.");
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    source.Open();
    destination.Open();
    source.BackupDatabase(destination);
    using var check = destination.CreateCommand();
    check.CommandText = "PRAGMA integrity_check";
    if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
        throw new InvalidDataException("Snapshot failed SQLite integrity_check.");
}

static byte[] HashFile(string path)
{
    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return SHA256.HashData(stream);
}
