using System.Windows.Threading;
using nexIRC.Application;

namespace nexIRC.Desktop;

public sealed class WpfWorkspaceDispatcher(Dispatcher dispatcher) : IWorkspaceDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ValueTask InvokeAsync(Action action)
        => InvokeAsync(action, WorkspaceDispatchActionCategory.Other);

    public ValueTask InvokeAsync(Action action, WorkspaceDispatchActionCategory category)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        var priority = category is WorkspaceDispatchActionCategory.UserSelection
            or WorkspaceDispatchActionCategory.ReadState
            or WorkspaceDispatchActionCategory.OperationFeedback
            or WorkspaceDispatchActionCategory.UiDemoCommand
            ? DispatcherPriority.Input
            : DispatcherPriority.Background;
        return new ValueTask(_dispatcher.InvokeAsync(action, priority).Task);
    }
}
