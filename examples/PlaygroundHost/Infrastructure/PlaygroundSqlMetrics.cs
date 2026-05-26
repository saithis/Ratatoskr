using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace PlaygroundHost.Infrastructure;

/// <summary>Raw SQL helpers for playground scenarios (stable Ratatoskr table/column names).</summary>
public static class PlaygroundSqlMetrics
{
    public static Task<int> CountPoisonedOutboxAsync(
        DbContext db,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteScalarIntAsync(
            db,
            """SELECT COUNT(*) FROM "OutboxMessageEntity" WHERE "IsPoisoned" = true""",
            cancellationToken
        );

    public static Task<int> CountPoisonedInboxAsync(
        DbContext db,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM \"InboxHandlerStatusEntity\" s INNER JOIN \"InboxMessageEntity\" m ON s.\"MessageId\" = m.\"Id\" WHERE s.\"IsPoisoned\" = true",
            cancellationToken
        );

    /// <summary>Poisoned outbox rows whose serialized properties include the scenario run id (concurrent-safe).</summary>
    public static Task<int> CountPoisonedOutboxForScenarioRunAsync(
        DbContext db,
        string scenarioRunId,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteScalarIntAsync(
            db,
            $"SELECT COUNT(*) FROM \"OutboxMessageEntity\" WHERE \"IsPoisoned\" = true AND \"SerializedProperties\" LIKE '%{EscapeLike(scenarioRunId)}%'",
            cancellationToken
        );

    public static Task<int> CountPoisonedInboxForScenarioRunAsync(
        DbContext db,
        string scenarioRunId,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteScalarIntAsync(
            db,
            $"SELECT COUNT(*) FROM \"InboxHandlerStatusEntity\" s INNER JOIN \"InboxMessageEntity\" m ON s.\"MessageId\" = m.\"Id\" WHERE s.\"IsPoisoned\" = true AND (m.\"SerializedProperties\" LIKE '%{EscapeLike(scenarioRunId)}%' OR encode(m.\"Content\", 'escape') LIKE '%{EscapeLike(scenarioRunId)}%')",
            cancellationToken
        );

    private static string EscapeLike(string s) =>
        s.Replace("'", "''", StringComparison.OrdinalIgnoreCase);

    [SuppressMessage(
        "IDisposableAnalyzers",
        "IDISP001:Dispose created",
        Justification = "The connection is owned by the DbContext; disposing it would break subsequent EF operations on the same context."
    )]
    private static async Task<int> ExecuteScalarIntAsync(
        DbContext db,
        string sql,
        CancellationToken cancellationToken
    )
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
#pragma warning disable CA2100
            // This is just a demo, so it is fine
            cmd.CommandText = sql;
#pragma warning restore CA2100
            var o = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(o, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (opened)
            {
                await conn.CloseAsync();
            }
        }
    }
}
