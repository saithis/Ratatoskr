using Microsoft.EntityFrameworkCore;

namespace Ratatoskr.EfCore.Internal;

/// <summary>
/// Shared helper for detecting well-known database exception types across providers.
/// </summary>
internal static class DbExceptionHelper
{
    /// <summary>
    /// Determines whether a <see cref="DbUpdateException"/> was caused by a unique constraint violation.
    /// Supports PostgreSQL (23505), SQLite (UNIQUE), and SQL Server (2601/2627).
    /// </summary>
    public static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner == null) return false;

        // Check provider-specific exception types first (more reliable than message sniffing).
        // PostgreSQL: Npgsql.PostgresException has a SqlState property.
        var exType = inner.GetType();
        var sqlStateProp = exType.GetProperty("SqlState");
        if (sqlStateProp?.GetValue(inner) is string sqlState && sqlState == "23505")
            return true;

        // SQL Server: Microsoft.Data.SqlClient.SqlException has a Number property.
        var numberProp = exType.GetProperty("Number");
        if (numberProp?.GetValue(inner) is int errorNumber && errorNumber is 2601 or 2627)
            return true;

        // SQLite fallback: check message for UNIQUE constraint text.
        var msg = inner.Message;
        return msg.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }
}
