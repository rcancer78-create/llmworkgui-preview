using LLMWorkGUI.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace LLMWorkGUI.IntegrationTests;

internal static class TestSqlitePool
{
    public static void Clear(ISqliteConnectionFactory factory)
    {
        using var connection = factory.CreateConnection();
        SqliteConnection.ClearPool(connection);
    }
}
