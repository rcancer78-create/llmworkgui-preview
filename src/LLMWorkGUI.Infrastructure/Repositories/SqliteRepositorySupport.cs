using System.Globalization;
using LLMWorkGUI.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.Infrastructure.Repositories;

internal static class SqliteRepositorySupport
{
    public static string CanonicalizeRootPath(string rootPath)
    {
        return ProjectLock.CanonicalizeRoot(rootPath);
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static string? FormatTimestamp(DateTimeOffset? value)
    {
        return value is null ? null : FormatTimestamp(value.Value);
    }

    public static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static DateTimeOffset? ParseNullableTimestamp(string? value)
    {
        return value is null ? null : ParseTimestamp(value);
    }

    public static string FormatEnum(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.ToString();
    }

    public static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new InvalidDataException($"'{value}' is not a valid {typeof(TEnum).Name} value.");
        }

        return parsed;
    }

    public static void AddNullable(SqliteCommand command, string parameterName, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
    }

    public static string? GetNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    }
}
