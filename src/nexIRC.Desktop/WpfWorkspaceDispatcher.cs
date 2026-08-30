using System.Windows.Threading;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed class WpfWorkspaceDispatcher(Dispatcher dispatcher) : IWorkspaceDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_dispatcher.InvokeAsync(action).Task);
    }
}
