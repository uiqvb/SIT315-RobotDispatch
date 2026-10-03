using Npgsql;
using RobotControllerApi.Infrastructure.DataAccess;

namespace RobotControllerApi.Infrastructure;

// Cross-replica mutual exclusion held by PostgreSQL, so every API replica agrees on one holder.
//
// The lock is transaction scoped (pg_try_advisory_xact_lock). PostgreSQL drops it when the
// transaction ends, and a crashed replica's connection closing ends that transaction, so the
// lock can never be left held by a process that no longer exists.
public class PostgresAdvisoryLock
{
    private readonly DbConfig _dbConfig;

    public PostgresAdvisoryLock(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    // Returns a handle while held, or null if another session holds the key. Never waits.
    public async Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken cancellationToken = default)
    {
        var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        try
        {
            await conn.OpenAsync(cancellationToken);
            var tx = await conn.BeginTransactionAsync(cancellationToken); //lock lives exactly as long as this transaction

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key);", conn, tx);
            cmd.Parameters.AddWithValue("key", key);

            var acquired = (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!; //true = this session now holds the key
            if (acquired) return new Handle(conn, tx);

            await tx.DisposeAsync();
            await conn.DisposeAsync();
            return null;
        }
        catch
        {
            await conn.DisposeAsync(); //closing the connection rolls back the transaction and frees the lock
            throw;
        }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;

        public Handle(NpgsqlConnection conn, NpgsqlTransaction tx)
        {
            _conn = conn;
            _tx = tx;
        }

        public async ValueTask DisposeAsync()
        {
            await _tx.DisposeAsync(); //rolling back the empty transaction releases the lock
            await _conn.DisposeAsync();
        }
    }
}
