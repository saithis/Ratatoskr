using Ratatoskr;

namespace PlaygroundApi.Events;

[RatatoskrMessage("com.example.add.note")]
public class AddNoteCommand
{
    public required string Text { get; init; }
}
