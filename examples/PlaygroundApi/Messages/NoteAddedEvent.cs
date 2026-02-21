using Ratatoskr;

namespace PlaygroundApi.Messages;

[RatatoskrMessage("com.example.notes.added")]
public class NoteAddedEvent
{
    public required int Id { get; init; }
    public required string Text { get; init; }
}
