using Rhino;

namespace RhinoAgent.Runtime;

internal static class RhinoTaskPump
{
    public static T Run<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        using var escapeCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            escapeCancellation.Token);
        var task = Task.Run(
            () => operation(linkedCancellation.Token),
            CancellationToken.None);

        EventHandler escapeHandler = (_, _) => escapeCancellation.Cancel();
        RhinoApp.EscapeKeyPressed += escapeHandler;

        try
        {
            // Keep Rhino's native message loop alive without starting a
            // secondary Get operation. GetCancel.Wait changes the command
            // prompt to "Command:" and applies Rhino's wait cursor, which is
            // visually noisy for a long-running Agent turn.
            while (!task.IsCompleted)
            {
                RhinoApp.Wait();
                Thread.Sleep(10);
            }

            return task.GetAwaiter().GetResult();
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= escapeHandler;
        }
    }
}
