using System.Security.Cryptography;
using System.Text;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Infrastructure.Concurrency;

public static class NamedMutexNames
{
    public const string LocalNamespacePrefix = @"Local\";
    public const string SupervisorNamePrefix = "LLMWorkGUI_Supervisor_";
    public const string CheckoutNamePrefix = "LLMWorkGUI_Checkout_";

    public static string ForSupervisor(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);

        return LocalNamespacePrefix
            + SupervisorNamePrefix
            + ComputeHash(NormalizeCanonicalPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDataDirectory))));
    }

    public static string ForCheckout(string canonicalRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRootPath);

        return LocalNamespacePrefix
            + CheckoutNamePrefix
            + ComputeHash(NormalizeCanonicalPath(ProjectLock.CanonicalizeRoot(canonicalRootPath)));
    }

    public static string ComputeHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string NormalizeCanonicalPath(string canonicalPath)
    {
        return OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath;
    }
}
