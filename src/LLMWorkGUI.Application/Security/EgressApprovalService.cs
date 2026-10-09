using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LLMWorkGUI.Application.Providers;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Concurrency;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;

namespace LLMWorkGUI.Application.Security;

/// <summary>Consent authority lives in this instance, never in caller-owned preview DTOs.</summary>
public sealed class EgressApprovalService(IProjectRepository projects, IRouteRepository routes,
    IProviderProfileRepository profiles, IWorkflowSecretScanner scanner, TimeProvider clock,
    IApplicationInstanceGuard guard) : IEgressApprovalService
{
    public const int MaxFragments = 64;
    public const int MaxPayloadBytes = 200_000;
    public const int MaxPendingPreviews = 128;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly CredentialTextRedactor _redactor = new();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Pending> _pending = [];

    public async Task<EgressPreview> PrepareAsync(EgressTarget target, IReadOnlyList<EgressFragmentInput> fragments,
        CancellationToken cancellationToken = default)
    {
        if (projects is null || routes is null || profiles is null || scanner is null || clock is null || guard is null)
            throw new EgressApprovalException();
        guard.EnsureSupervisorPermitted();
        var snapshot = Snapshot(fragments);
        var policy = await ResolvePolicyAsync(target, snapshot, cancellationToken).ConfigureAwait(false);
        var sanitized = snapshot.Select(fragment => new EgressPreviewFragment(fragment.Id,
            _redactor.RedactDiagnostic(fragment.Label), _redactor.RedactDiagnostic(fragment.Content), "", fragment.Classification)).ToArray();
        var bytes = sanitized.ToDictionary(f => f.Id + ".txt", f => StrictUtf8.GetBytes(f.Content), StringComparer.Ordinal);
        var report = await scanner.ScanFilesAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (report.HasFindings && report.Findings.Count == 0
            || report.Findings.Any(f => !bytes.ContainsKey(f.RelativePath))) throw new EgressApprovalException();
        // A scanner finding is excluded, not disclosed through its snippet or an exception message.
        for (var i = 0; i < sanitized.Length; i++)
        {
            var fragment = sanitized[i];
            var lines = fragment.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            foreach (var finding in report.Findings.Where(f => f.RelativePath == fragment.Id + ".txt"))
            {
                if (finding.LineNumber < 1 || finding.LineNumber > lines.Length) throw new EgressApprovalException();
                lines[finding.LineNumber - 1] = CredentialTextRedactor.Placeholder;
            }
            var content = report.Findings.Any(f => f.RelativePath == fragment.Id + ".txt")
                ? string.Join("\n", lines) : fragment.Content;
            sanitized[i] = new(fragment.Id, fragment.Label, content, Hash(StrictUtf8.GetBytes(content)), fragment.Classification);
        }
        var verified = sanitized.ToDictionary(f => f.Id + ".txt", f => StrictUtf8.GetBytes(f.Content), StringComparer.Ordinal);
        var verification = await scanner.ScanFilesAsync(verified, cancellationToken).ConfigureAwait(false);
        if (verification.HasFindings || sanitized.Any(f => _redactor.ContainsSensitiveData(f.Content))
            || verified.Values.Sum(b => b.Length) > MaxPayloadBytes) throw new EgressApprovalException();
        var labelScan = await scanner.ScanFilesAsync(sanitized.ToDictionary(f => f.Id + ".txt",
            f => StrictUtf8.GetBytes(f.Label), StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        if (labelScan.HasFindings)
        {
            for (var i = 0; i < sanitized.Length; i++)
            {
                var f = sanitized[i];
                sanitized[i] = new(f.Id, "Фрагмент", f.Content, f.ContentSha256, f.Classification);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        guard.EnsureSupervisorPermitted();
        var preview = new EgressPreview(Guid.NewGuid(), target, Array.AsReadOnly(sanitized),
            FragmentHash(sanitized), clock.GetUtcNow() + PreviewLifetime);
        lock (_sync)
        {
            Prune();
            if (_pending.Count >= MaxPendingPreviews) throw new EgressApprovalException();
            _pending.Add(preview.Id, new(preview, SourceHash(snapshot), policy.Fingerprint, policy.Classification));
        }
        return preview;
    }

    public void ApproveFragment(Guid previewId, string fragmentId, string displayedContentSha256)
    {
        guard.EnsureSupervisorPermitted();
        lock (_sync)
        {
            Prune();
            if (!_pending.TryGetValue(previewId, out var pending) || pending.Reserved
                || !pending.Preview.Fragments.Any(f => f.Id == fragmentId && f.ContentSha256 == displayedContentSha256))
                throw new EgressApprovalException();
            pending.Approved.Add(fragmentId);
        }
    }

    public void Revoke(Guid previewId) { lock (_sync) _pending.Remove(previewId); }

    public async Task<ApprovedEgressPayload> ConsumeAsync(Guid previewId, EgressTarget target,
        IReadOnlyList<EgressFragmentInput> currentFragments, CancellationToken cancellationToken = default)
    {
        guard.EnsureSupervisorPermitted();
        Pending pending;
        lock (_sync)
        {
            Prune();
            if (!_pending.TryGetValue(previewId, out pending!) || pending.Reserved) throw new EgressApprovalException();
            pending.Reserved = true;
        }
        try
        {
            var snapshot = Snapshot(currentFragments);
            if (pending.Preview.Target != target || pending.SourceHash != SourceHash(snapshot)
                || pending.Approved.Count != pending.Preview.Fragments.Count) throw new EgressApprovalException();
            var policy = await ResolvePolicyAsync(target, snapshot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            guard.EnsureSupervisorPermitted();
            lock (_sync)
            {
                if (!_pending.TryGetValue(previewId, out var current) || !ReferenceEquals(current, pending)
                    || clock.GetUtcNow() >= pending.Preview.ExpiresAtUtc || pending.PolicyFingerprint != policy.Fingerprint)
                    throw new EgressApprovalException();
                _pending.Remove(previewId);
                return new(pending.Preview, pending.Classification, pending.PolicyFingerprint);
            }
        }
        finally
        {
            // Failed/cancelled attempts also invalidate consent; no silent retry or resurrection.
            lock (_sync) _pending.Remove(previewId);
        }
    }

    private async Task<(string Fingerprint, DataClassification Classification)> ResolvePolicyAsync(EgressTarget target,
        EgressFragmentInput[] fragments, CancellationToken token)
    {
        if (target is null || target.Binding is null || string.IsNullOrWhiteSpace(target.ProjectId)
            || string.IsNullOrWhiteSpace(target.RootPath) || string.IsNullOrWhiteSpace(target.RouteId)
            || !Enum.IsDefined(target.Policy)) throw new EgressApprovalException();
        var project = await projects.GetByIdAsync(target.ProjectId, token).ConfigureAwait(false);
        var assignment = await routes.GetAssignmentAsync(target.RouteId, token).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(target.Binding.ProviderProfileId, token).ConfigureAwait(false);
        if (project is null || !Enum.IsDefined(project.DataClassification) || assignment is null || !assignment.HasEveryIdentity
            || !assignment.Route.IsEnabled || assignment.Route.Binding != target.Binding
            || !Enum.IsDefined(assignment.Route.MaxDataClass)
            || !string.Equals(CanonicalRoot(project.RootPath), CanonicalRoot(target.RootPath), StringComparison.OrdinalIgnoreCase))
            throw new EgressApprovalException();
        var classification = (DataClassification)Math.Max((int)project.DataClassification, fragments.Max(f => (int)f.Classification));
        if (!ProviderDataPolicy.Evaluate(classification, profile?.Id, profile, target.Binding.Backend).IsAllowed
            || classification > assignment.Route.MaxDataClass) throw new EgressApprovalException();
        if (classification == DataClassification.Restricted
            && (target.Policy != RoutingPolicy.ManualOnly || !HasFragmentOnlyTransport(target))) throw new EgressApprovalException();
        // Configuration changes invalidate consent even if they would remain permissive.
        return (EgressPolicyFingerprint.Compute(project, assignment.Route, profile!), classification);
    }

    private static string CanonicalRoot(string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root)) throw new EgressApprovalException();
            return ProjectLock.CanonicalizeRoot(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EgressApprovalException();
        }
    }

    internal static bool HasFragmentOnlyTransport(EgressTarget target) => target.Binding.Backend == BackendType.NativeGateway
        && target.Binding.ProviderProfileId == GrokBotRestrictions.ProviderProfileId
        && target.Binding.ReasoningEffort is null && target.Binding.SpeedMode is null && target.Binding.ExecutionMode is null;

    private static EgressFragmentInput[] Snapshot(IReadOnlyList<EgressFragmentInput> fragments)
    {
        if (fragments is null || fragments.Count is < 1 or > MaxFragments) throw new EgressApprovalException();
        var snapshot = fragments.ToArray();
        if (snapshot.Length is < 1 or > MaxFragments) throw new EgressApprovalException();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var f in snapshot)
        {
            if (f is null || f.Id is null || f.Id.Length is < 1 or > 64 || f.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                || !ids.Add(f.Id) || f.Label is null || f.Label.Length > 256 || f.Content is null
                || !Enum.IsDefined(f.Classification)) throw new EgressApprovalException();
            try { bytes += StrictUtf8.GetByteCount(f.Content); _ = StrictUtf8.GetByteCount(f.Label); }
            catch (EncoderFallbackException) { throw new EgressApprovalException(); }
            if (bytes > MaxPayloadBytes) throw new EgressApprovalException();
        }
        return snapshot;
    }

    private static string SourceHash(EgressFragmentInput[] fragments) => Hash(JsonSerializer.SerializeToUtf8Bytes(fragments));
    private static string FragmentHash(EgressPreviewFragment[] fragments) => Hash(JsonSerializer.SerializeToUtf8Bytes(fragments));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var id in _pending.Where(pair => pair.Value.Preview.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray()) _pending.Remove(id);
    }
    private sealed class Pending(EgressPreview preview, string sourceHash, string fingerprint, DataClassification classification)
    {
        public EgressPreview Preview { get; } = preview;
        public string SourceHash { get; } = sourceHash;
        public string PolicyFingerprint { get; } = fingerprint;
        public DataClassification Classification { get; } = classification;
        public HashSet<string> Approved { get; } = new(StringComparer.Ordinal);
        public bool Reserved { get; set; }
    }
}
