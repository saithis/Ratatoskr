using Ratatoskr;

namespace PlaygroundApi.Messages;

[RatatoskrMessage("com.example.add.note")]
public class AddNoteCommand
{
    public required string Text { get; init; }
}
