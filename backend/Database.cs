using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace Drive;

public sealed class Database(NpgsqlDataSource source)
{
    public async Task<Session> Open(CancellationToken ct = default)
    {
        var connection = await source.OpenConnectionAsync(ct);
        return new Session(connection);
    }

    public async Task Initialize()
    {
        await using var session = await Open();
        await session.Begin();
        await session.Execute("SELECT pg_advisory_xact_lock(410027)");
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("Drive.schema.sql")!;
        using var reader = new StreamReader(stream);
        await session.Execute(await reader.ReadToEndAsync());
        await session.Commit();
    }
}

public sealed class Session(NpgsqlConnection connection) : IAsyncDisposable
{
    private NpgsqlTransaction? transaction;
    public async Task Begin() => transaction = await connection.BeginTransactionAsync();
    public async Task Commit() => await transaction!.CommitAsync();

    private NpgsqlCommand Command(string sql, object?[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(value is null
                ? new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = DBNull.Value }
                : new NpgsqlParameter { Value = value });
        }
        command.CommandTimeout = 120;
        return command;
    }

    public async Task<int> Execute(string sql, params object?[] values)
    {
        await using var command = Command(sql, values);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Row>> Query(string sql, params object?[] values)
    {
        await using var command = Command(sql, values);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync())
        {
            var row = new Row();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task<Row?> One(string sql, params object?[] values) =>
        (await Query(sql, values)).FirstOrDefault();

    public async ValueTask DisposeAsync()
    {
        if (transaction is not null)
        {
            await transaction.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}

public sealed class Row : Dictionary<string, object?>
{
    public string Text(string key) => (string)this[key]!;
    public Guid Id(string key) => (Guid)this[key]!;
    public Guid? OptionalId(string key) => this[key] is Guid value ? value : null;
    public long Number(string key) => Convert.ToInt64(this[key]);
}
