using AwesomeAssertions;
using Ratatoskr.EfCore;
using Ratatoskr.EfCore.Testing;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Testing;

public class OutboxStagingAssertionsTests
{
    [Test]
    public void ShouldHaveStaged_WithMatchingMessage_DoesNotThrow()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Id = "test-1", Data = "staged" });

        // Act & Assert
        collection.ShouldHaveStaged<TestEvent>();
    }

    [Test]
    public void ShouldHaveStaged_WithPredicate_DoesNotThrow()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Id = "test-1", Data = "staged" });

        // Act & Assert
        collection.ShouldHaveStaged<TestEvent>(e => e.Data == "staged");
    }

    [Test]
    public void ShouldHaveStaged_NoMatchingMessage_Throws()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new OrderCreatedEvent { OrderId = Guid.NewGuid(), Amount = 100 });

        // Act & Assert
        var act = () => collection.ShouldHaveStaged<TestEvent>();
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected to find a staged message of type TestEvent*");
    }

    [Test]
    public void ShouldHaveStaged_PredicateNotMatching_Throws()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Data = "wrong" });

        // Act & Assert
        var act = () => collection.ShouldHaveStaged<TestEvent>(e => e.Data == "expected");
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*but none matched the predicate*");
    }

    [Test]
    public void ShouldNotHaveStaged_WhenEmpty_DoesNotThrow()
    {
        // Arrange
        var collection = new OutboxStagingCollection();

        // Act & Assert
        collection.ShouldNotHaveStaged<TestEvent>();
    }

    [Test]
    public void ShouldNotHaveStaged_WhenMessageExists_Throws()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Data = "staged" });

        // Act & Assert
        var act = () => collection.ShouldNotHaveStaged<TestEvent>();
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected no staged messages of type TestEvent*");
    }

    [Test]
    public void ShouldHaveStagedCount_WithCorrectCount_DoesNotThrow()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Data = "one" });
        collection.Add(new TestEvent { Data = "two" });

        // Act & Assert
        collection.ShouldHaveStagedCount(2);
    }

    [Test]
    public void ShouldHaveStagedCount_WithIncorrectCount_Throws()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Data = "one" });

        // Act & Assert
        var act = () => collection.ShouldHaveStagedCount(3);
        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*Expected 3 staged message(s), but found 1*");
    }

    [Test]
    public void ShouldNotHaveStagedAny_WhenEmpty_DoesNotThrow()
    {
        // Arrange
        var collection = new OutboxStagingCollection();

        // Act & Assert
        collection.ShouldNotHaveStagedAny();
    }

    [Test]
    public void ShouldNotHaveStagedAny_WhenHasMessages_Throws()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        collection.Add(new TestEvent { Data = "staged" });

        // Act & Assert
        var act = () => collection.ShouldNotHaveStagedAny();
        act.Should().Throw<RatatoskrTestException>();
    }
}
