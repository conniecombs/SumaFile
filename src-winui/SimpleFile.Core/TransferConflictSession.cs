namespace SimpleFile.Core;

public sealed class TransferConflictSession
{
    public string? StickyAction { get; private set; }

    public bool TryGetSticky(out string action)
    {
        if (!string.IsNullOrEmpty(StickyAction))
        {
            action = StickyAction;
            return true;
        }

        action = "";
        return false;
    }

    public void Remember(string action, bool applyToAll)
    {
        if (applyToAll && !string.IsNullOrWhiteSpace(action))
        {
            StickyAction = action;
        }
    }
}
