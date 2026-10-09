using System.IO;
using LLMWorkGUI.App.Services;
using LLMWorkGUI.Application.Repositories;
using LLMWorkGUI.Ui.Tests.TestSupport;
using Xunit;

namespace LLMWorkGUI.Ui.Tests;

public sealed class ThemePersistenceOrderRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewestRequestedThemeOwnsPersistedValueAndErrorAfterAnOlderWriteSettles(bool firstFails)
    {
        var repository = new DeferredSettings(firstFails);
        var service = new ThemeService(repository, new FakeSystemThemeProvider(), new RecordingThemeResourceApplier());
        var older = service.SetThemeAsync(AppTheme.Dark);
        try
        {
            await repository.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newer = service.SetThemeAsync(AppTheme.Light);
            repository.Release.TrySetResult();
            await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(AppTheme.Light, service.CurrentTheme);
            Assert.Equal("Light", repository.Value);
            Assert.Null(service.LastPersistenceError);
        }
        finally
        {
            repository.Release.TrySetResult();
            await older;
        }
    }

    private sealed class DeferredSettings(bool firstFails) : IApplicationSettingsRepository
    {
        private int _calls;
        public string? Value;
        public readonly TaskCompletionSource FirstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public async Task SetValueAsync(string key, string value, string valueType = "String", CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstEntered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (firstFails) throw new IOException("Synthetic earlier persistence failure");
            }
            Value = value;
        }
        public Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}
