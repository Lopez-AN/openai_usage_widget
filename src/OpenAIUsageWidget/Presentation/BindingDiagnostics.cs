using System.Diagnostics;

namespace OpenAIUsageWidget.Presentation;

/// <summary>Only enabled in explicitly offline demo/smoke modes. Never captures real account data.</summary>
public sealed class BindingDiagnostics : TraceListener
{
    public List<string> Errors { get; } = [];
    public BindingDiagnostics() => PresentationTraceSources.DataBindingSource.Listeners.Add(this);
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
    public override void WriteLine(string? message) => Write(message);
    protected override void Dispose(bool disposing)
    {
        if (disposing) PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
