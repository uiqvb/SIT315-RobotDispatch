using Npgsql;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

// The four async query shapes the dispatch path needs. Each awaits Npgsql directly, so the
// request thread goes back to the pool while PostgreSQL works, instead of blocking on it.
internal static class AdoAsync
{
    public static async Task<T?> QuerySingleAsync<T>(DbConfig dbConfig, string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct) where T : class
    {
        await using var conn = new NpgsqlConnection(dbConfig.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var dr = await cmd.ExecuteReaderAsync(ct);
        return await dr.ReadAsync(ct) ? map(dr) : null;
    }

    public static async Task<List<T>> QueryListAsync<T>(DbConfig dbConfig, string sql, Action<NpgsqlCommand> bind, Func<NpgsqlDataReader, T> map, CancellationToken ct)
    {
        var results = new List<T>();
        await using var conn = new NpgsqlConnection(dbConfig.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var dr = await cmd.ExecuteReaderAsync(ct);
        while (await dr.ReadAsync(ct)) results.Add(map(dr));
        return results;
    }

    public static async Task<object?> ScalarAsync(DbConfig dbConfig, string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(dbConfig.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        return await cmd.ExecuteScalarAsync(ct);
    }

    // Returns rows affected.
    public static async Task<int> ExecuteAsync(DbConfig dbConfig, string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(dbConfig.GetConnectionString());
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
