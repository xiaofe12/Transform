using System;

namespace Transform.Core;

/// <summary>One failed restore step must not prevent the remaining steps from running.</summary>
internal sealed class CleanupSequence
{
    private readonly Action<string, Exception> _report;
    internal bool Succeeded { get; private set; } = true;
    internal CleanupSequence(Action<string, Exception> report) { _report = report; }

    internal void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Succeeded = false;
            try { _report?.Invoke(name, ex); } catch { /* Logging must not stop cleanup either. */ }
        }
    }
}
