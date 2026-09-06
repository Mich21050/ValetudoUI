namespace ValetudoUI.Services;

/// <summary>Per-circuit (scoped) transient notifications.</summary>
public sealed class ToastService
{
    public sealed record Toast(Guid Id, string Message, string Kind);

    private readonly List<Toast> _items = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Toast> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public event Action? Changed;

    public void Error(string message) => Add(message, "error");
    public void Info(string message) => Add(message, "info");
    public void Success(string message) => Add(message, "success");

    private void Add(string message, string kind)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        lock (_lock) _items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(kind == "error" ? 8000 : 4000).ContinueWith(_ => Remove(toast.Id));
    }

    public void Remove(Guid id)
    {
        bool removed;
        lock (_lock) removed = _items.RemoveAll(t => t.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }
}
