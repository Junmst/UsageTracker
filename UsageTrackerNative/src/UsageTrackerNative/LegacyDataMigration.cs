using Microsoft.Data.Sqlite;

namespace UsageTrackerNative;

/// <summary>
/// 一次性迁移：把历史目录（UsageTrackerNative_v2 / UsageTrackerNative）的数据库、
/// settings.json、web-preferences.json 合并到唯一目录 %LocalAppData%\时迹。
/// 目标库已存在时绝不重复迁移，更不会回退到旧目录建新库。
/// </summary>
internal static class LegacyDataMigration
{
    private const string DbFileName = "usage-tracker.db";
    private const string SettingsFileName = "settings.json";
    private const string WebPreferencesFileName = "web-preferences.json";
    private static readonly string[] LegacyDirectoryNames = ["UsageTrackerNative_v2", "UsageTrackerNative"];

    public static void Run(string localAppData, string targetDirectory)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            MigrateDatabase(localAppData, targetDirectory);
            MigrateFile(localAppData, targetDirectory, SettingsFileName);
            MigrateFile(localAppData, targetDirectory, WebPreferencesFileName);
        }
        catch (Exception ex)
        {
            NativeLogger.LogStartupException("LegacyDataMigration.Run", ex);
        }
    }

    private static void MigrateDatabase(string localAppData, string targetDirectory)
    {
        var targetDb = Path.Combine(targetDirectory, DbFileName);
        if (File.Exists(targetDb)) return;

        var sources = LegacyDirectoryNames
            .Select(name => Path.Combine(localAppData, name, DbFileName))
            .Where(File.Exists)
            .ToList();
        if (sources.Count == 0) return;

        // 按修改时间取最新的库为主库（v2 是旧库的超集）
        var primary = sources.OrderByDescending(File.GetLastWriteTime).First();
        Checkpoint(primary);
        File.Copy(primary, targetDb);

        // 其余历史库中独有的记录按 Id 补齐
        using var connection = new SqliteConnection($"Data Source={targetDb}");
        connection.Open();
        foreach (var source in sources.Where(path => !string.Equals(path, primary, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Checkpoint(source);
                var escaped = source.Replace("'", "''");
                using var attach = connection.CreateCommand();
                attach.CommandText = $"ATTACH DATABASE '{escaped}' AS legacy_db";
                attach.ExecuteNonQuery();

                using var mergeSessions = connection.CreateCommand();
                mergeSessions.CommandText = "INSERT OR IGNORE INTO main.UsageSessions SELECT * FROM legacy_db.UsageSessions";
                mergeSessions.ExecuteNonQuery();

                using var detach = connection.CreateCommand();
                detach.CommandText = "DETACH DATABASE legacy_db";
                detach.ExecuteNonQuery();
                NativeLogger.LogStartupMessage("LegacyDataMigration.MigrateDatabase", $"已合并历史库: {source}");
            }
            catch (Exception ex)
            {
                NativeLogger.LogStartupException($"LegacyDataMigration.MigrateDatabase:{source}", ex);
            }
        }

        NativeLogger.LogStartupMessage("LegacyDataMigration.MigrateDatabase", $"迁移完成，主库: {primary}");
    }

    private static void MigrateFile(string localAppData, string targetDirectory, string fileName)
    {
        var target = Path.Combine(targetDirectory, fileName);
        if (File.Exists(target)) return;

        var source = LegacyDirectoryNames
            .Select(name => Path.Combine(localAppData, name, fileName))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (source is null) return;

        File.Copy(source, target);
        NativeLogger.LogStartupMessage("LegacyDataMigration.MigrateFile", $"{fileName} <- {source}");
    }

    /// <summary>打开源库执行 TRUNCATE checkpoint，把 WAL 内容并入主 db 文件，避免只复制主库丢数据。</summary>
    private static void Checkpoint(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }
}
