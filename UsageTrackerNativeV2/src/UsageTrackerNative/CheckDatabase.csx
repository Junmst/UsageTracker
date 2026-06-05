
#r "nuget: Microsoft.Data.Sqlite"

using Microsoft.Data.Sqlite;
using System;

var dbPath = @"C:\Users\27960\AppData\Local\UsageTrackerNative\usage-tracker.db";
Console.WriteLine($"Checking database: {dbPath}");
Console.WriteLine($"Database exists: {System.IO.File.Exists(dbPath)}");
Console.WriteLine();

using var connection = new SqliteConnection($"Data Source={dbPath}");
connection.Open();

// 设置 PRAGMA
using var pragma = connection.CreateCommand();
pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
pragma.ExecuteNonQuery();

// 检查表结构
Console.WriteLine("=== Table Structure ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "PRAGMA table_info(UsageSessions);";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"Column {reader.GetInt32(0)}: {reader.GetString(1)} ({reader.GetString(2)})");
    }
}
Console.WriteLine();

// 检查记录数量
Console.WriteLine("=== Record Counts ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM UsageSessions;";
    var count = Convert.ToInt32(cmd.ExecuteScalar());
    Console.WriteLine($"Total records: {count}");
}

using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM UsageSessions WHERE IsDeleted = 0;";
    var count = Convert.ToInt32(cmd.ExecuteScalar());
    Console.WriteLine($"Active records: {count}");
}

using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(*) FROM ActiveSession;";
    var count = Convert.ToInt32(cmd.ExecuteScalar());
    Console.WriteLine($"Active session records: {count}");
}
Console.WriteLine();

// 显示最近的记录
Console.WriteLine("=== Latest 10 Records ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT Id, ProcessName, StartTime, EndTime FROM UsageSessions ORDER BY StartTime DESC LIMIT 10;";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"  {reader.GetString(0)} - {reader.GetString(1)} - {reader.GetString(2)} - { (reader.IsDBNull(3) ? "null" : reader.GetString(3)) }");
    }
}
Console.WriteLine();

// 检查活跃会话
Console.WriteLine("=== Active Session ===");
using (var cmd = connection.CreateCommand())
{
    cmd.CommandText = "SELECT * FROM ActiveSession;";
    using var reader = cmd.ExecuteReader();
    if (reader.Read())
    {
        Console.WriteLine($"  Found active session: {reader.GetString(1)} - {reader.GetString(2)}");
    }
    else
    {
        Console.WriteLine("  No active session");
    }
}
