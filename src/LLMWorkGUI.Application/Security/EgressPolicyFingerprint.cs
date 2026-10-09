using System.Security.Cryptography;
using System.Text.Json;
using LLMWorkGUI.Domain.Entities;

namespace LLMWorkGUI.Application.Security;

/// <summary>Deterministic metadata hash, not consent. Dispatch recomputes it under its writer transaction.</summary>
public static class EgressPolicyFingerprint
{
    public static string Compute(Project project, Route route, ProviderProfile profile) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { project.Id, project.RootPath, project.DataClassification, Route = route, Profile = profile })));
}
