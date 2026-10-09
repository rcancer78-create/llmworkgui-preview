using LLMWorkGUI.Application.Security;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Application.Workflows.Declarative;
using LLMWorkGUI.Application.Workflows.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LLMWorkGUI.IntegrationTests.Workflows;

public sealed partial class WorkflowReviewProvenanceTests
{
    [Theory]
    [InlineData("restricted")]
    [InlineData("profile-limit")]
    [InlineData("disabled-profile")]
    [InlineData("route-limit")]
    [InlineData("missing-gate")]
    [InlineData("restricted-artifact")]
    public async Task ReviewerDispatchCannotBypassDataClassification(string scenario)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog(new[] { channel }));
            if (scenario == "missing-gate") services.RemoveAll<IDataClassificationGate>();
        });
        await StartRunAsync(provider, RealRouteId, scenario == "restricted-artifact"
            ? DataClassification.Restricted : DataClassification.PrivateSource);
        await ChangeReviewDataPolicyAsync(scenario);
        var before = await ReviewerRowCountsAsync();

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(RunId);

        Assert.Equal(WorkflowReviewRequestOutcome.Refused, result.Outcome);
        Assert.Equal(0, channel.Calls);
        Assert.Equal(before, await ReviewerRowCountsAsync());
    }

    [Theory]
    [InlineData("restricted")]
    [InlineData("profile-limit")]
    [InlineData("disabled-profile")]
    [InlineData("route-limit")]
    public async Task AdmissionRevalidatesDataPolicyChangedAfterPreflight(string scenario)
    {
        var channel = new DispatchSafetyChannel();
        using var provider = CreateProvider(services =>
        {
            services.AddSingleton<IWorkflowChannelCatalog>(new WorkflowChannelCatalog(new[] { channel }));
            services.AddSingleton<IDataClassificationGate>(container => new PolicyChangingGate(
                new DataClassificationGate(container.GetRequiredService<IProviderProfileRepository>()),
                () => ChangeReviewDataPolicyAsync(scenario)));
        });
        await StartRunAsync(provider, RealRouteId);
        var before = await ReviewerRowCountsAsync();

        var result = await provider.GetRequiredService<IWorkflowReviewRequestService>()
            .RequestAssignedReviewAsync(RunId);

        Assert.Equal(WorkflowReviewRequestOutcome.Refused, result.Outcome);
        Assert.Equal(0, channel.Calls);
        Assert.Equal(before, await ReviewerRowCountsAsync());
    }

    private async Task ChangeReviewDataPolicyAsync(string scenario)
    {
        await using (var connection = await _database.Factory.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = scenario switch
            {
                "restricted" => "UPDATE Projects SET DataClassification='Restricted'",
                "profile-limit" => "UPDATE ProviderProfiles SET MaxDataClass='PublicSource'",
                "disabled-profile" => "UPDATE ProviderProfiles SET IsEnabled=0",
                "route-limit" => "UPDATE Routes SET MaxDataClass='PublicSource'",
                _ => "SELECT 1"
            };
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class PolicyChangingGate(IDataClassificationGate inner, Func<Task> change) : IDataClassificationGate
    {
        public async Task<DataClassificationGateDecision> EvaluateAsync(DataClassification projectDataClass,
            string? providerProfileId, bool isManualOnly = false, CancellationToken cancellationToken = default)
        {
            var decision = await inner.EvaluateAsync(projectDataClass, providerProfileId, isManualOnly, cancellationToken);
            Assert.True(decision.IsAllowed);
            await change();
            return decision;
        }
    }
}
