using System.Windows;
using System.Windows.Threading;

namespace LLMWorkGUI.Ui.Tests.TestSupport;

internal static class StaTestRunner
{
    private static readonly Lazy<Dispatcher> DispatcherHost = new(
        CreateDispatcherHost,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        DispatcherHost.Value.Invoke(action);
    }

    public static T Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        return DispatcherHost.Value.Invoke(func);
    }

    public static void EnsureApplication()
    {
        Run(() =>
        {
            if (System.Windows.Application.Current is null)
            {
                _ = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }
        });
    }

    private static Dispatcher CreateDispatcherHost()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LLMWorkGUI.Ui.Tests.StaHost"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        return dispatcher!;
    }
}
