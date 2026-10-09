using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;

namespace LLMWorkGUI.Application.Security;

/// <summary>The requested destination, not evidence of native response identity.</summary>
public sealed record EgressTarget(string ProjectId, string RootPath, string RouteId,
    SessionBinding Binding, RoutingPolicy Policy)
{
    public override string ToString() => "Egress target (destination withheld)";
}

public sealed record EgressFragmentInput(string Id, string Label, string Content, DataClassification Classification)
{
    public override string ToString() => "Egress fragment input (content withheld)";
}

public sealed class EgressPreviewFragment
{
    internal EgressPreviewFragment(string id, string label, string content, string hash, DataClassification classification)
        => (Id, Label, Content, ContentSha256, Classification) = (id, label, content, hash, classification);
    public string Id { get; }
    public string Label { get; }
    public string Content { get; }
    public string ContentSha256 { get; }
    public DataClassification Classification { get; }
    public override string ToString() => $"Egress preview fragment ({ContentSha256})";
}

public sealed class EgressPreview
{
    internal EgressPreview(Guid id, EgressTarget target, IReadOnlyList<EgressPreviewFragment> fragments,
        string payloadHash, DateTimeOffset expiresAt)
        => (Id, Target, Fragments, PayloadSha256, ExpiresAtUtc) = (id, target, fragments, payloadHash, expiresAt);
    public Guid Id { get; }
    public EgressTarget Target { get; }
    public IReadOnlyList<EgressPreviewFragment> Fragments { get; }
    public string PayloadSha256 { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public override string ToString() => $"Egress preview ({Id}, {PayloadSha256})";
}

/// <summary>Created only by the approval service after all fragments were approved and consumed once.
/// Consumers must transmit these sanitized bytes, revalidate admission, and prevent unpreviewed reads.</summary>
public sealed class ApprovedEgressPayload
{
    internal ApprovedEgressPayload(EgressPreview preview, DataClassification classification, string policyFingerprint)
        => (PreviewId, Target, Fragments, PayloadSha256, ExpiresAtUtc, Classification, PolicyFingerprint) =
            (preview.Id, preview.Target, preview.Fragments, preview.PayloadSha256, preview.ExpiresAtUtc, classification, policyFingerprint);
    public Guid PreviewId { get; }
    public string PolicyFingerprint { get; }
    public EgressTarget Target { get; }
    public IReadOnlyList<EgressPreviewFragment> Fragments { get; }
    public string PayloadSha256 { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public DataClassification Classification { get; }
    public override string ToString() => $"Approved egress payload ({PayloadSha256})";
}

/// <summary>In-memory, expiring, single-attempt consent for exact sanitized fragments and destination.
/// This is not a boolean ManualOnly override and is not a replacement for atomic dispatch admission.</summary>
public interface IEgressApprovalService
{
    Task<EgressPreview> PrepareAsync(EgressTarget target, IReadOnlyList<EgressFragmentInput> fragments,
        CancellationToken cancellationToken = default);
    void ApproveFragment(Guid previewId, string fragmentId, string displayedContentSha256);
    void Revoke(Guid previewId);
    Task<ApprovedEgressPayload> ConsumeAsync(Guid previewId, EgressTarget target,
        IReadOnlyList<EgressFragmentInput> currentFragments, CancellationToken cancellationToken = default);
}

public sealed class EgressApprovalException : InvalidOperationException
{
    public EgressApprovalException() : base("Передача данных не разрешена. Обновите предпросмотр и подтвердите каждый фрагмент для выбранного маршрута.") { }
}
