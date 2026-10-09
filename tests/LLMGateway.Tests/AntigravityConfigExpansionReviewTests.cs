using System.Reflection;
using LLMGateway.Core;
using LLMGateway.Native;
using LLMGateway.Native.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
namespace LLMGateway.Tests;
public sealed class AntigravityConfigExpansionReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApiKeyHomeCannotExpandSelectedCredentialOrCredentialShapedValue(bool registered)
    {
        var root = Directory.CreateTempSubdirectory("owned-agy-review-");
        var variable = "OWNED_AGY_" + Guid.NewGuid().ToString("N");
        var value = registered ? "ownedOpaqueCredential" : "sk-ownedSyntheticCredential12345";
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var account = new AccountProfile { Id="owned", Provider=ProviderKind.Antigravity,
                AuthMode=AccountAuthMode.ApiKeyFromEnvironment, ApiKeyVariable=variable,
                ConfigDirectory=Path.Combine(root.FullName,"%" + variable + "%") };
            var adapter = new AntigravityAdapter(new ExecutableResolver(), new GatewayOptions(), NullLogger<AntigravityAdapter>.Instance);
            var method = typeof(AntigravityAdapter).GetMethod("BuildEnvironment", BindingFlags.Instance|BindingFlags.NonPublic)!;
            var outer = Assert.Throws<TargetInvocationException>(() => method.Invoke(adapter, new object[] { account, false }));
            var error = Assert.IsType<GatewayException>(outer.InnerException);
            Assert.Equal(GatewayErrorKind.InvalidRequest,error.Kind);
            Assert.DoesNotContain(value,error.Message);
            Assert.False(Directory.Exists(Path.Combine(root.FullName,value)));
        }
        finally { Environment.SetEnvironmentVariable(variable,null); root.Delete(true); }
    }
    [Fact]
    public void LiteralHomeStillResolvesWithoutReadingOrChangingRealUserSettings()
    {
        var root=Directory.CreateTempSubdirectory("owned-agy-literal-");
        try { Assert.Equal(root.FullName,AntigravityAdapter.ResolveApiKeyHome(new AccountProfile { Id="owned",ConfigDirectory=root.FullName })); }
        finally { root.Delete(true); }
    }
}
