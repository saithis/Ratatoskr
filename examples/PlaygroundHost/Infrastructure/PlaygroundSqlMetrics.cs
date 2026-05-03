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

    /// <summary>Poisoned outbox rows whose serialized properties include the scenario run id (concurrent-safe).</summary>
    public static Task<int> CountPoisonedOutboxForScenarioRunAsync(
        DbContext db,
        string scenarioRunId,
        CancellationToken cancellationToken = default) =>
        ExecuteScalarIntAsync(
            db,
            $"""
            SELECT COUNT(*) FROM "OutboxMessages"
            WHERE "IsPoisoned" = true
              AND "SerializedProperties" LIKE '%{EscapeLike(scenarioRunId)}%'
            """,
            cancellationToken);

    public static Task<int> CountPoisonedInboxForScenarioRunAsync(
        DbContext db,
        string scenarioRunId,
        CancellationToken cancellationToken = default) =>
        ExecuteScalarIntAsync(
            db,
            $"""
            SELECT COUNT(*) FROM "InboxHandlerStatuses" s
            INNER JOIN "InboxMessages" m ON s."MessageId" = m."Id"
            WHERE s."IsPoisoned" = true
              AND (m."SerializedProperties" LIKE '%{EscapeLike(scenarioRunId)}%' OR encode(m."Content", 'escape') LIKE '%{EscapeLike(scenarioRunId)}%')
            """,
            cancellationToken);

    private static string EscapeLike(string s) => s.Replace("'", "''");

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
