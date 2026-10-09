using System.IO.Compression;
using System.Text;
using LLMWorkGUI.Application.Workflows;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Infrastructure.Workflows;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed class WorkflowManifestParserTests
{
    private readonly WorkflowManifestParser _parser = new();

    [Fact]
    public async Task ParseManifestAsync_ParsesFormalWorkflowManifest()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(
            archive,
            "workflow.json",
            """
            {
              "id": "wf-42",
              "version": "2.1.0",
              "name": "Release Flow",
              "entrypoints": ["run.ps1", { "path": "scripts/entrypoint.sh" }],
              "declaredRoles": ["Executor", "Reviewer"],
              "bindings": { "provider": "opencode" },
              "metadata": { "createdBy": "tester" }
            }
            """));

        var manifest = await ParseAsync(archiveBytes);

        Assert.True(manifest.HasFormalManifest);
        Assert.Equal("workflow.json", manifest.ManifestPath);
        Assert.Equal("wf-42", manifest.WorkflowId);
        Assert.Equal("2.1.0", manifest.WorkflowVersion);
        Assert.Equal("Release Flow", manifest.Name);
        Assert.Equal(
            new[] { "run.ps1", "scripts/entrypoint.sh" },
            manifest.Entrypoints.Select(entrypoint => entrypoint.Path).ToArray());
        Assert.All(manifest.Entrypoints, entrypoint => Assert.True(entrypoint.IsDeclared));
        Assert.Equal(WorkflowEntrypointKind.PowerShell, manifest.Entrypoints[0].Kind);
        Assert.Equal(WorkflowEntrypointKind.Shell, manifest.Entrypoints[1].Kind);
        Assert.Equal(new[] { WorkflowRole.Executor, WorkflowRole.Reviewer }, manifest.DeclaredRoles.ToArray());
        Assert.NotNull(manifest.BindingsJson);
        Assert.Contains("opencode", manifest.BindingsJson!, StringComparison.Ordinal);
        Assert.NotNull(manifest.CreationMetadataJson);
        Assert.Contains("tester", manifest.CreationMetadataJson!, StringComparison.Ordinal);
        Assert.Equal("[\"run.ps1\",\"scripts/entrypoint.sh\"]", manifest.EntrypointsJson);
        Assert.Equal("[\"Executor\",\"Reviewer\"]", manifest.DeclaredRolesJson);
    }

    [Fact]
    public async Task ParseManifestAsync_PrefersWorkflowJsonOverManifestJson()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "manifest.json", "{\"id\":\"from-manifest\"}");
            AddEntry(archive, "workflow.json", "{\"id\":\"from-workflow\"}");
        });

        var manifest = await ParseAsync(archiveBytes);

        Assert.Equal("workflow.json", manifest.ManifestPath);
        Assert.Equal("from-workflow", manifest.WorkflowId);
    }

    [Fact]
    public async Task ParseManifestAsync_UsesManifestJsonWhenWorkflowJsonIsMissing()
    {
        var archiveBytes = CreateArchive(archive =>
            AddEntry(archive, "manifest.json", "{\"workflowId\":\"legacy-id\"}"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.Equal("manifest.json", manifest.ManifestPath);
        Assert.Equal("legacy-id", manifest.WorkflowId);
    }

    [Fact]
    public async Task ParseManifestAsync_ReadsEntrypointObjectsUsingFallbackProperties()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(
            archive,
            "workflow.json",
            "{\"entrypoints\":[{\"file\":\"start.sh\"},{\"entry\":\"main.py\"}]}"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.Equal(
            new[] { "start.sh", "main.py" },
            manifest.Entrypoints.Select(entrypoint => entrypoint.Path).ToArray());
    }

    [Fact]
    public async Task ParseManifestAsync_IgnoresUnknownDeclaredRoles()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(
            archive,
            "workflow.json",
            "{\"declaredRoles\":[\"executor\",\"Wizard\",\"Unknown\",\"REVIEWER\"]}"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.Equal(new[] { WorkflowRole.Executor, WorkflowRole.Reviewer }, manifest.DeclaredRoles.ToArray());
        Assert.Equal("[\"Executor\",\"Reviewer\"]", manifest.DeclaredRolesJson);
    }

    [Fact]
    public async Task ParseManifestAsync_ThrowsForMalformedManifestJson()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "workflow.json", "{ not json"));

        await Assert.ThrowsAsync<WorkflowValidationException>(() => ParseAsync(archiveBytes));
    }

    [Fact]
    public async Task ParseManifestAsync_ThrowsWhenManifestRootIsNotAnObject()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "workflow.json", "[]"));

        await Assert.ThrowsAsync<WorkflowValidationException>(() => ParseAsync(archiveBytes));
    }

    [Fact]
    public async Task ParseManifestAsync_ThrowsForNonZipStream()
    {
        using var stream = new MemoryStream(new byte[] { 0x01, 0x02, 0x03, 0x04 });

        await Assert.ThrowsAsync<WorkflowValidationException>(() => _parser.ParseManifestAsync(stream));
    }

    [Fact]
    public async Task ParseManifestAsync_FormalManifestSuppressesLegacyHeuristics()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "workflow.json", "{\"name\":\"formal\"}");
            AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            AddEntry(archive, "README.md", "The Reviewer checks the output.");
        });

        var manifest = await ParseAsync(archiveBytes);

        Assert.True(manifest.HasFormalManifest);
        Assert.Empty(manifest.Entrypoints);
        Assert.Empty(manifest.DeclaredRoles);
    }

    [Fact]
    public async Task ParseManifestAsync_DiscoversLegacyEntrypointsByKnownNames()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "run.ps1", "Write-Host 'hi'");
            AddEntry(archive, "scripts/entrypoint.sh", "echo hi");
            AddEntry(archive, "main.py", "print('hi')");
            AddEntry(archive, "custom-tool.ps1", "Write-Host 'custom'");
            AddEntry(archive, "notes.txt", "plain");
        });

        var manifest = await ParseAsync(archiveBytes);

        Assert.False(manifest.HasFormalManifest);
        Assert.Equal(
            new[] { "main.py", "run.ps1", "scripts/entrypoint.sh" },
            manifest.Entrypoints.Select(entrypoint => entrypoint.Path).ToArray());
        Assert.Equal(WorkflowEntrypointKind.Python, manifest.Entrypoints[0].Kind);
        Assert.Equal(WorkflowEntrypointKind.PowerShell, manifest.Entrypoints[1].Kind);
        Assert.Equal(WorkflowEntrypointKind.Shell, manifest.Entrypoints[2].Kind);
        Assert.All(manifest.Entrypoints, entrypoint => Assert.False(entrypoint.IsDeclared));
        Assert.Equal("[\"main.py\",\"run.ps1\",\"scripts/entrypoint.sh\"]", manifest.EntrypointsJson);
    }

    [Fact]
    public async Task ParseManifestAsync_DoesNotGuessUnknownScriptsAsEntrypoints()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "custom-tool.ps1", "Write-Host 'custom'");
            AddEntry(archive, "helper.tool", "noop");
        });

        var manifest = await ParseAsync(archiveBytes);

        Assert.Empty(manifest.Entrypoints);
    }

    [Fact]
    public async Task ParseManifestAsync_DiscoversRolesInPrimaryDocumentation()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(
            archive,
            "README.md",
            """
            # Workflow

            - The Coordinator keeps the plan.
            - The Executor performs the work.
            - The Reviewer checks the result.
            - The Escalation handles blockers.
            - Unknown is not a role.
            """));

        var manifest = await ParseAsync(archiveBytes);

        Assert.Equal(
            new[] { WorkflowRole.Coordinator, WorkflowRole.Executor, WorkflowRole.Reviewer, WorkflowRole.Escalation },
            manifest.DeclaredRoles.ToArray());
        Assert.Equal("[\"Coordinator\",\"Executor\",\"Reviewer\",\"Escalation\"]", manifest.DeclaredRolesJson);
    }

    [Fact]
    public async Task ParseManifestAsync_RoleDiscoveryIgnoresWordFragments()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(
            archive,
            "README.md",
            "Executors and reviewers coordinate the work."));

        var manifest = await ParseAsync(archiveBytes);

        Assert.Empty(manifest.DeclaredRoles);
    }

    [Fact]
    public async Task ParseManifestAsync_RoleDiscoveryUsesDocumentationPriority()
    {
        var archiveBytes = CreateArchive(archive =>
        {
            AddEntry(archive, "README.md", "This document names no roles.");
            AddEntry(archive, "WORKFLOW.md", "The Reviewer checks the result.");
        });

        var manifest = await ParseAsync(archiveBytes);

        Assert.Empty(manifest.DeclaredRoles);
    }

    [Fact]
    public async Task ParseManifestAsync_YieldsEmptyArraysWhenNothingIsDiscovered()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "notes.txt", "plain text"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.False(manifest.HasFormalManifest);
        Assert.Null(manifest.ManifestPath);
        Assert.Empty(manifest.Entrypoints);
        Assert.Empty(manifest.DeclaredRoles);
        Assert.Equal("[]", manifest.EntrypointsJson);
        Assert.Equal("[]", manifest.DeclaredRolesJson);
    }

    [Fact]
    public async Task ParseManifestAsync_FormalManifestWithoutEntrypointsYieldsEmptyArrays()
    {
        var archiveBytes = CreateArchive(archive => AddEntry(archive, "workflow.json", "{}"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.True(manifest.HasFormalManifest);
        Assert.Equal("[]", manifest.EntrypointsJson);
        Assert.Equal("[]", manifest.DeclaredRolesJson);
        Assert.Null(manifest.BindingsJson);
        Assert.Null(manifest.CreationMetadataJson);
    }

    [Fact]
    public async Task ParseManifestAsync_IgnoresNestedManifestFiles()
    {
        var archiveBytes = CreateArchive(archive =>
            AddEntry(archive, "nested/workflow.json", "{\"id\":\"nested\"}"));

        var manifest = await ParseAsync(archiveBytes);

        Assert.False(manifest.HasFormalManifest);
    }

    private async Task<WorkflowManifest> ParseAsync(byte[] archiveBytes)
    {
        using var stream = new MemoryStream(archiveBytes);

        return await _parser.ParseManifestAsync(stream);
    }

    private static byte[] CreateArchive(Action<ZipArchive> configure)
    {
        return WorkflowTestArchiveFactory.CreateArchive(configure);
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
