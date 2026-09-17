using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PdnodeVote.Data;

/// <summary>
/// Helpers for turning unique-constraint races into predictable outcomes.
/// </summary>
/// <remarks>
/// Several flows follow a read-then-insert pattern ("check whether this row exists, then add it").
/// Two concurrent requests can both pass the check, and the second insert then violates a unique
/// index / composite primary key. Without handling, that surfaces as an unhandled
/// <see cref="DbUpdateException"/> and an HTTP 500; the correct outcome is to treat the loser of the
/// race as a duplicate.
/// </remarks>
public static class DbUpdateExceptionExtensions
{
    /// <summary>True when the failure is a SQLite uniqueness violation.</summary>
    public static bool IsUniqueConstraintViolation(this DbUpdateException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 19 }) // SQLITE_CONSTRAINT
            {
                return true;
            }

            // Provider-agnostic fallback (other providers phrase it differently).
            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the failure is a unique violation on the given key.</summary>
    public static bool IsUniqueConstraintViolation(this DbUpdateException ex, params string[] keyColumns)
    {
        if (!ex.IsUniqueConstraintViolation())
        {
            return false;
        }

        var text = ex.ToString();
        return keyColumns.All(column => text.Contains(column, StringComparison.OrdinalIgnoreCase));
    }
}
