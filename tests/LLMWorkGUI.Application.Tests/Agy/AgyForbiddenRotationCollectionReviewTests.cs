using LLMWorkGUI.Application.Agy;
using LLMWorkGUI.Domain.Enums;
using LLMWorkGUI.Domain.ValueObjects;
using Xunit;

namespace LLMWorkGUI.Application.Tests.Agy;

[CollectionDefinition("AGY exported rotation policy mutation", DisableParallelization = true)]
public sealed class AgyExportedRotationPolicyMutationCollection
{
}

[Collection("AGY exported rotation policy mutation")]
public sealed class AgyForbiddenRotationCollectionReviewTests
{
    [Fact]
    public async Task CallerCannotRemoveTheNextBanThroughTheExportedCollectionAndReachTheUtility()
    {
        // Exercise the exported surface without requiring a particular future collection implementation.
        object exported = AgyProfilePolicy.ForbiddenRotationCommands;
        var list = exported as IList<string>;
        string? original = null;
        var mutated = false;
        try
        {
            if (list is { Count: > 0 })
            {
                original = list[0];
                try
                {
                    list[0] = "owned-unrelated-profile";
                    mutated = true;
                }
                catch (NotSupportedException)
                {
                    // A truly read-only collection may refuse mutation; the real bridge must still refuse next.
                }
            }

            var utility = new OwnedUtilityTrap();
            var bridge = new AgyProfileAccountBridge(utility);
            var binding = new SessionBinding(BackendType.Agy, "owned-provider", "owned-active-profile",
                "owned-model", "high", "fast", "accept-edits");
            var result = await bridge.PinAccountAsync("owned-provider", "next", binding);

            Assert.Empty(utility.SwitchCalls);
            Assert.Equal(0, utility.CurrentCalls);
            Assert.False(result.IsPinned);
            Assert.Null(result.ConfirmedBinding);
        }
        finally
        {
            // The dedicated nonparallel collection plus restoration prevents a RED run from polluting other tests.
            if (mutated) list![0] = original!;
        }
    }

    private sealed class OwnedUtilityTrap : IAgyProfileService
    {
        public bool IsAvailable => true;
        public string? ExecutablePath => null;
        public string? AvailabilityBlocker => null;
        public List<string> SwitchCalls { get; } = new();
        public int CurrentCalls { get; private set; }

        public Task<string?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
        {
            CurrentCalls++;
            return Task.FromResult<string?>("owned-active-profile");
        }

        public Task<IReadOnlyList<AgyProfileSummary>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgyProfileSummary>>(Array.Empty<AgyProfileSummary>());

        public Task<AgyProfileSwitchResult> SwitchProfileAsync(string profileName, CancellationToken cancellationToken = default)
        {
            SwitchCalls.Add(profileName);
            return Task.FromResult(AgyProfileSwitchResult.Success(profileName));
        }
    }
}
