using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Application.Workflows.Orchestration;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Repositories;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowRunArtifactGateTests
{
    [Fact]
    public async Task Review_OrdinaryArtifactOverTwentyMiBStopsBeforeDurableEvidenceOrBlobMutation()
    {
        var run = await StartRunAtDocumentReviewAsync();
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<IWorkflowRunService>();
        var store = new WorkflowBlobStore(_database.Root);
        var beforeFiles = Directory.GetFiles(store.BlobsDirectory, "*", SearchOption.AllDirectories).Order().ToArray();
        using var content = new GeneratedArtifactStream(WorkflowRunService.MaxExecutionArtifactBytes + 64 * 1024);

        var failure = await Record.ExceptionAsync(() => service.RecordStageArtifactAsync(
            run.Id, StageId, ArtifactKind, content, DataClassification.PrivateSource));

        var reloaded = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal(run.Artifacts.Count, reloaded!.Artifacts.Count);
        Assert.Equal(beforeFiles, Directory.GetFiles(store.BlobsDirectory, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.IsType<WorkflowValidationException>(failure);
        Assert.Equal(WorkflowRunService.MaxExecutionArtifactBytes + 1L, content.BytesRead);
        Assert.Equal(StageId, reloaded.CurrentStageId);
    }

    [Fact]
    public async Task Review_OrdinaryArtifactAtTwentyMiBIsStoredAndByteVerifiedAfterReopen()
    {
        var run = await StartRunAtDocumentReviewAsync();
        using var provider = CreateProvider();
        using var content = new GeneratedArtifactStream(WorkflowRunService.MaxExecutionArtifactBytes);
        var updated = await provider.GetRequiredService<IWorkflowRunService>().RecordStageArtifactAsync(
            run.Id, StageId, ArtifactKind, content, DataClassification.PrivateSource);

        var artifact = SingleArtifact(updated);
        var reopened = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        Assert.Equal(WorkflowRunService.MaxExecutionArtifactBytes, artifact.SizeBytes);
        Assert.Equal(artifact.BlobId, SingleArtifact(reopened!).BlobId);
        Assert.True(await new WorkflowBlobStore(_database.Root).VerifyBlobAsync(artifact.BlobId));
        Assert.Null(artifact.ExecutionId); // This ordinary stage path does not assert native execution proof.
    }

    [Theory]
    [InlineData(-36500)]
    [InlineData(36500)]
    public async Task Review_ApprovalTimeComesFromTheHostAndSurvivesSqliteReopen(int callerDayOffset)
    {
        var run = await StartRunAtDocumentReviewAsync();
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<IWorkflowRunService>();
        var reviewed = await RecordArtifactAsync(run.Id, "reviewed bundle");
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ReviewerRole, reviewed.HashSha256));
        await service.RecordLegacyUnlinkedReviewerVerdictAsync(run.Id, Verdict(WorkflowScheme.ArchitectRole, reviewed.HashSha256));
        await service.AdvanceStageAsync(run.Id, "documents reviewed");
        var evidence = await RecordArtifactAtAsync(run.Id, WorkflowScheme.UserApprovalStageId,
            "ApprovedDocument", "approved document");
        var now = new DateTimeOffset(2036, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var stampedService = new WorkflowRunService(
            provider.GetRequiredService<IWorkflowRunRepository>(), WorkflowScheme.CreateStandardDevelopmentScheme(),
            provider.GetRequiredService<IWorkflowTemplateStore>(), provider.GetRequiredService<IWorkflowArtifactBlobStore>(),
            new ReviewApprovalIdentity(), new ReviewApprovalClock(now));

        await stampedService.RecordUserApprovalAsync(run.Id, Approval(evidence.HashSha256, now.AddDays(callerDayOffset)));

        var reloaded = await new SqliteWorkflowRunRepository(_database.Factory).GetByIdAsync(run.Id);
        var approval = Assert.Single(reloaded!.Approvals);
        Assert.Equal(now, approval.DecidedAtUtc);
        Assert.Equal("host-local-principal", approval.ApprovedBy);
        Assert.Equal(evidence.HashSha256, approval.ArtifactHash);
    }

    private sealed class ReviewApprovalIdentity : IUserApprovalIdentity
    {
        public string GetCurrentApproverIdentity() => "host-local-principal";
    }

    private sealed class ReviewApprovalClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class GeneratedArtifactStream(long byteCount) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = (int)Math.Min(count, byteCount - BytesRead);
            buffer.AsSpan(offset, available).Clear();
            BytesRead += available;
            return available;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = (int)Math.Min(buffer.Length, byteCount - BytesRead);
            buffer.Span[..available].Clear();
            BytesRead += available;
            return ValueTask.FromResult(available);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
