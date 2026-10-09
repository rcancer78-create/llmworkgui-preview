using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Application.Workflows.Studio;
using LLMWorkGUI.Domain.Entities;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LLMWorkGUI.VisibleWorkflowHarness;

/// <summary>
/// The isolated project, imported workflow package, active binding and built-in template assignment the
/// acceptance run starts from.
/// <para>
/// Every row is written through the repositories the product registers, and the assignment is made with
/// <see cref="IWorkflowStudioService.AssignTemplateToProjectAsync"/> - selecting the template in the Studio
/// would only make it selectable, and the shipped start command would then refuse with
/// <c>no-template-assignment</c>.
/// </para>
/// <para>
/// Nothing here is model evidence: there is no route, no session, no execution, no reviewer verdict and no
/// approval. A stage whose gate needs one of those stays refused, which is the honest outcome for an
/// isolated run that must not invent observations.
/// </para>
/// </summary>
internal static class AcceptanceSeed
{
    public const string ProjectId = "visible-acceptance-project";
    public const string PackageId = "visible-acceptance-package";
    public const string VersionId = "visible-acceptance-version";

    private static readonly DateTimeOffset SeededAt = new(2026, 9, 29, 7, 0, 0, TimeSpan.Zero);

    /// <summary>Seeds the project, its imported package, the active binding and the assignment.</summary>
    public static async Task SeedAsync(IServiceProvider services, string projectRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        Directory.CreateDirectory(projectRoot);

        var blobStore = services.GetRequiredService<WorkflowBlobStore>();
        var zip = CreatePackageZip();
        var blobId = WorkflowBlobStore.ComputeBlobId(zip);

        await using (var content = new MemoryStream(zip, writable: false))
        {
            await blobStore.SaveBlobAsync(content, cancellationToken).ConfigureAwait(false);
        }

        await services.GetRequiredService<IProjectRepository>()
            .UpsertAsync(
                new Project(
                    ProjectId,
                    "Visible acceptance project",
                    projectRoot,
                    gitBranch: null,
                    isDirty: false,
                    hasRequiredInstructions: false,
                    defaultWorkflowId: null,
                    defaultRoutePolicyId: null,
                    DataClassification.PrivateSource),
                cancellationToken)
            .ConfigureAwait(false);

        await services.GetRequiredService<IWorkflowPackageRepository>()
            .UpsertAsync(
                new WorkflowPackage(
                    PackageId,
                    "visible-acceptance.zip",
                    "Isolated package imported only for the visible acceptance run.",
                    Array.Empty<string>(),
                    WorkflowSourceType.ZipArchive,
                    blobId,
                    blobId,
                    SeededAt,
                    SeededAt),
                cancellationToken)
            .ConfigureAwait(false);

        await services.GetRequiredService<IWorkflowVersionRepository>()
            .UpsertAsync(
                new WorkflowVersion(
                    VersionId,
                    PackageId,
                    versionNumber: 1,
                    blobId: blobId,
                    originalHash: blobId,
                    WorkflowSourceType.ZipArchive,
                    entrypointsJson: null,
                    declaredRolesJson: null,
                    bindingsJson: null,
                    compatibilityReportJson: null,
                    creationMetadataJson: null,
                    createdAtUtc: SeededAt,
                    activatedAtUtc: null),
                cancellationToken)
            .ConfigureAwait(false);

        // A binding id is a GUID by domain rule; the project-to-package pointer is the only mutable row.
        await services.GetRequiredService<IWorkflowBindingRepository>()
            .UpsertAsync(
                new WorkflowBinding(
                    Guid.NewGuid().ToString(),
                    ProjectId,
                    PackageId,
                    VersionId,
                    routePolicyId: null,
                    SeededAt,
                    SeededAt),
                cancellationToken)
            .ConfigureAwait(false);

        // The assignment is what makes a run startable. It goes through the production studio service so the
        // run pins the version the store actually holds.
        var assignment = await services.GetRequiredService<IWorkflowStudioService>()
            .AssignTemplateToProjectAsync(
                ProjectId,
                WorkflowStudioService.StandardTemplateId,
                templateVersion: 1,
                cancellationToken)
            .ConfigureAwait(false);

        if (!assignment.IsAssigned)
        {
            throw new InvalidOperationException(
                $"The built-in template could not be assigned: {assignment.TemplateId}@{assignment.TemplateVersion}.");
        }
    }

    /// <summary>A real ZIP blob: the shipped preview and tree read their bytes from this file.</summary>
    private static byte[] CreatePackageZip()
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "workflow.yaml", "name: Visible acceptance package\nversion: 1\n");
            WriteEntry(archive, "README.md", "# Visible acceptance package\n\nImported for the acceptance run only.\n");
            WriteEntry(archive, "docs/task.md", "# Task\n\nSeeded document for the pinned run.\n");
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>
    /// Writes a real file with real bytes for one stage and returns its path and SHA-256. The harness never
    /// types a hash: the file is what the shipped attach control reads.
    /// </summary>
    public static (string Path, string Sha256) WriteStageDocument(
        string directory,
        string fileName,
        string artifactKind,
        string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        var bytes = Encoding.UTF8.GetBytes(
            $"# {artifactKind}{Environment.NewLine}{Environment.NewLine}"
            + $"Visible acceptance document attached by the product control for stage '{stageId}'.{Environment.NewLine}"
            + $"Marker: {artifactKind.Length * 1000 + stageId.Length}.{Environment.NewLine}");

        File.WriteAllBytes(path, bytes);

        return (path, "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
