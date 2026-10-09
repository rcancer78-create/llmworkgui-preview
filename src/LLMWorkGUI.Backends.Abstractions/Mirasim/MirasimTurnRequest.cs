namespace LLMWorkGUI.Backends.Abstractions.Mirasim;

public sealed record MirasimTurnRequest
{
    public const string Redacted = "[REDACTED]";

    public required string SessionKey { get; init; }

    public required string Prompt { get; init; }

    public string? ExecutionMode { get; init; }

    public required string RequestedHarness { get; init; }

    public required string RequestedModelId { get; init; }

    public string? RequestedAccount { get; init; }

    public string? RequestedRouteLeg { get; init; }

    public required string ProjectId { get; init; }

    public string? ProviderProfileId { get; init; }

    public string? RequestedRouteId { get; init; }

    public required string CanonicalRootPath { get; init; }

    public required string ExecutionId { get; init; }

    public long ProcessGeneration { get; init; }

    public string? AuthToken { get; init; }

    public override string ToString() =>
        "MirasimTurnRequest { " +
        $"SessionKey = {SessionKey}, " +
        $"Prompt = {Redacted}, " +
        $"ExecutionMode = {ExecutionMode ?? "null"}, " +
        $"RequestedHarness = {RequestedHarness}, " +
        $"RequestedModelId = {RequestedModelId}, " +
        $"RequestedAccount = {RequestedAccount ?? "null"}, " +
        $"RequestedRouteLeg = {RequestedRouteLeg ?? "null"}, " +
        $"ProjectId = {ProjectId}, " +
        $"CanonicalRootPath = {CanonicalRootPath}, " +
        $"ExecutionId = {ExecutionId}, " +
        $"ProcessGeneration = {ProcessGeneration}, " +
        $"AuthToken = {Redacted} }}";
}
