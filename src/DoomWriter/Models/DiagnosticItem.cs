namespace DoomWriter.Models;

/// <summary>
/// A diagnostic item to display in the editor (spelling, grammar, suggestion, entity link).
/// Sent to the JavaScript editor for underline rendering and quick-action popups.
/// </summary>
public record DiagnosticItem
{
    public required DiagnosticType Type { get; init; }
    public required string Word { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int Length { get; init; }
    public required string Message { get; init; }
    public List<string> Suggestions { get; init; } = [];
    public string? QuickFixText { get; init; }
}

public enum DiagnosticType
{
    Spelling,
    Grammar,
    Suggestion,
    EntityLink
}

/// <summary>
/// Request from the editor for quick action at a specific position.
/// Triggered by Ctrl+. or clicking the margin lightbulb.
/// </summary>
public record QuickActionRequest
{
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required string Word { get; init; }
}
