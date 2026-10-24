using AwesomeAssertions;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Inbox;

public class InboxBuilderTests
{
    private static InboxBuilder<TestDbContext> CreateBuilder() => new();

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(10)]
    public void WithMaxRetries_ZeroOrPositive_IsAllowed(int maxRetries)
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithMaxRetries(maxRetries);

        // Assert
        builder.Options.MaxRetries.Should().Be(maxRetries);
    }

    [Test]
    public void WithMaxRetries_Negative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var act = () => builder.WithMaxRetries(-1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
