using Rhino;

namespace RhinoAgent.Runtime;

internal static class RhinoUiDispatcher
{
    public static void Invoke(Action action) =>
        InvokeAsync(() =>
        {
            action();
            return true;
        }).GetAwaiter().GetResult();

    public static void Post(Action action)
    {
        if (RhinoApp.IsOnMainThread)
        {
            action();
            return;
        }

        try
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try
                {
                    action();
                }
                catch
                {
                    // Modeless output is best-effort while Rhino is closing.
                }
            }));
        }
        catch
        {
            // Rhino can reject new UI work during application shutdown.
        }
    }

    public static T Invoke<T>(Func<T> action) =>
        InvokeAsync(action).GetAwaiter().GetResult();

    public static Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (RhinoApp.IsOnMainThread)
            return Task.FromResult(action());

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }

        return completion.Task;
    }

}
