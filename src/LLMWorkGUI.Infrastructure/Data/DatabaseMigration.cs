using System.Security.Cryptography;
using System.Text;

namespace LLMWorkGUI.Infrastructure.Data;

public sealed record DatabaseMigration
{
    public DatabaseMigration(int version, string name, string sql)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Migration version must be a positive integer.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Version = version;
        Name = name;
        Sql = sql;
    }

    public int Version { get; }

    public string Name { get; }

    public string Sql { get; }

    public string Checksum =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();
}
