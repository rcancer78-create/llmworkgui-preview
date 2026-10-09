using LLMWorkGUI.Backends.Abstractions.OpenCode;
using LLMWorkGUI.Backends.OpenCode;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal sealed class FakeOpenCodeProcessConnection : IOpenCodeServerManager
{
    public Instance Process { get; } = new();
    public OpenCodeServerConnection Connection { get; }
    public FakeOpenCodeProcessConnection() => Connection = new(this);
    public Task<IOpenCodeServerInstance> StartServerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IOpenCodeServerInstance>(Process);
    public Task StopServerAsync(IOpenCodeServerInstance instance, CancellationToken cancellationToken = default)
    { Process.IsAlive = false; return Task.CompletedTask; }
    internal sealed class Instance : IOpenCodeServerInstance
    {
        public string InstanceId => "synthetic-opencode-process";
        public int? ProcessId => null;
        public long ProcessGeneration { get; set; } = 17;
        public int AssignedPort => 12345;
        public Uri BaseUrl => new("http://127.0.0.1:12345");
        public bool IsAlive { get; set; } = true;
        public DateTimeOffset StartedAtUtc => DateTimeOffset.UnixEpoch;
    }
}
