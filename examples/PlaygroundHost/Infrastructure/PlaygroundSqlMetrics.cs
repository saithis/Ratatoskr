using System.Data;
using Microsoft.EntityFrameworkCore;

namespace PlaygroundHost.Infrastructure;

/// <summary>Raw SQL helpers for playground scenarios (stable Ratatoskr table/column names).</summary>
public static class PlaygroundSqlMetrics
{
    public static Task<int> CountPoisonedOutboxAsync(DbContext db, CancellationToken cancellationToken = default) =>
        ExecuteScalarIntAsync(db, """SELECT COUNT(*) FROM "OutboxMessages" WHERE "IsPoisoned" = true""", cancellationToken);

    public static Task<int> CountPoisonedInboxAsync(DbContext db, CancellationToken cancellationToken = default) =>
        ExecuteScalarIntAsync(
            db,
            """
            SELECT COUNT(*) FROM "InboxHandlerStatuses" s
            INNER JOIN "InboxMessages" m ON s."MessageId" = m."Id"
            WHERE s."IsPoisoned" = true
            """,
            cancellationToken);

    private static async Task<int> ExecuteScalarIntAsync(DbContext db, string sql, CancellationToken cancellationToken)
    {
        var conn = db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(cancellationToken);
            opened = true;
        }

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var o = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(o);
        }
        finally
        {
            if (opened)
                await conn.CloseAsync();
        }
    }
}
