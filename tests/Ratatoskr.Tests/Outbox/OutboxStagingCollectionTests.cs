using AwesomeAssertions;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Outbox;

public class OutboxStagingCollectionTests
{
    [Test]
    public void Add_Generic_QueuesMessage()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        var message = new TestEvent { Data = "test" };

        // Act
        collection.Add(message);

        // Assert
        collection.Count.Should().Be(1);
        collection.StagedItems[0].Message.Should().Be(message);
    }

    [Test]
    public void Add_NonGeneric_QueuesMessage()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        object message = new TestEvent { Data = "test" };

        // Act
        collection.Add(message);

        // Assert
        collection.Count.Should().Be(1);
        collection.StagedItems[0].Message.Should().Be(message);
    }

    [Test]
    public void Add_WithProperties_StoresProperties()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        var message = new TestEvent { Data = "test" };
        var props = new MessageProperties { Type = "custom.type" };

        // Act
        collection.Add(message, props);

        // Assert
        collection.Count.Should().Be(1);
        collection.StagedItems[0].Message.Should().Be(message);
        collection.StagedItems[0].Properties.Type.Should().Be("custom.type");
    }

    [Test]
    public void Add_WithoutProperties_CreatesDefaultProperties()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        var message = new TestEvent { Data = "test" };

        // Act
        collection.Add(message);

        // Assert
        collection.Count.Should().Be(1);
        collection.StagedItems[0].Message.Should().Be(message);
        collection.StagedItems[0].Properties.Should().NotBeNull();
        collection.StagedItems[0].Properties.Type.Should().BeNull();
    }

    [Test]
    public void Count_ReturnsCorrectCount()
    {
        // Arrange
        var collection = new OutboxStagingCollection();

        // Act & Assert
        collection.Count.Should().Be(0);

        collection.Add(new TestEvent { Data = "1" });
        collection.Count.Should().Be(1);

        collection.Add(new TestEvent { Data = "2" });
        collection.Count.Should().Be(2);

        collection.Add(new TestEvent { Data = "3" });
        collection.Count.Should().Be(3);
    }

    [Test]
    public void Add_MultipleMessages_MaintainsOrder()
    {
        // Arrange
        var collection = new OutboxStagingCollection();
        var message1 = new TestEvent { Data = "first" };
        var message2 = new TestEvent { Data = "second" };
        var message3 = new TestEvent { Data = "third" };

        // Act
        collection.Add(message1);
        collection.Add(message2);
        collection.Add(message3);

        // Assert
        collection.Count.Should().Be(3);
        collection.StagedItems[0].Message.Should().Be(message1);
        collection.StagedItems[1].Message.Should().Be(message2);
        collection.StagedItems[2].Message.Should().Be(message3);
    }
}
