namespace nexIRC.Application;

public interface IWorkspaceDispatcher
{
    ValueTask InvokeAsync(Action action);
}

public sealed class ImmediateWorkspaceDispatcher : IWorkspaceDispatcher
{
    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return ValueTask.CompletedTask;
    }
}
