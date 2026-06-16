namespace PiSharp.Tui.Interactive;

public interface ITuiDispatcher
{
    void Post(Action action);
    object AddTimeout(TimeSpan interval, Func<bool> callback);
    void RemoveTimeout(object token);
}
