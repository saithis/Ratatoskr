using AwesomeAssertions;
using Ratatoskr.Core;
using Ratatoskr.Testing;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Testing;

public class FakeRatatoskrTests
{
    [Test]
    public async Task PublishDirectAsync_CapturesMessage()
    {
        var fake = new FakeRatatoskr();
        var message = new TestEvent { Data = "test" };

        await fake.PublishDirectAsync(message);

        fake.PublishedMessages.Should().HaveCount(1);
        fake.PublishedMessages[0].As<TestEvent>().Data.Should().Be("test");
        fake.PublishedMessages[0].MessageType.Should().Be(typeof(TestEvent));
    }

    [Test]
    public async Task PublishDirectAsync_CapturesProperties()
    {
        var fake = new FakeRatatoskr();
        var props = new MessageProperties { Type = "test.event", Source = "/test" };

        await fake.PublishDirectAsync(new TestEvent(), props);

        fake.PublishedMessages[0].Properties.Type.Should().Be("test.event");
        fake.PublishedMessages[0].Properties.Source.Should().Be("/test");
    }

    [Test]
    public async Task PublishDirectAsync_WithNullProperties_CreatesDefaultProperties()
    {
        var fake = new FakeRatatoskr();

        await fake.PublishDirectAsync(new TestEvent());

        fake.PublishedMessages[0].Properties.Should().NotBeNull();
    }

    [Test]
    public async Task PublishDirectAsync_MultipleCalls_CapturesAll()
    {
        var fake = new FakeRatatoskr();

        await fake.PublishDirectAsync(new TestEvent { Data = "first" });
        await fake.PublishDirectAsync(new TestEvent { Data = "second" });

        fake.PublishedMessages.Should().HaveCount(2);
    }

    [Test]
    public async Task Clear_RemovesAllMessages()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        fake.Clear();

        fake.PublishedMessages.Should().BeEmpty();
    }

    [Test]
    public async Task PublishedMessages_ReturnsSnapshot()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent { Data = "first" });

        var snapshot = fake.PublishedMessages;

        await fake.PublishDirectAsync(new TestEvent { Data = "second" });

        // The snapshot should not contain the second message
        snapshot.Should().HaveCount(1);
        fake.PublishedMessages.Should().HaveCount(2);
    }

    [Test]
    public async Task PublishDirectAsync_IsThreadSafe()
    {
        var fake = new FakeRatatoskr();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => fake.PublishDirectAsync(new TestEvent { Data = $"msg-{i}" }));

        await Task.WhenAll(tasks);

        fake.PublishedMessages.Should().HaveCount(100);
    }
}

public class FakeRatatoskrAssertionsTests
{
    [Test]
    public async Task ShouldHavePublished_WithMatchingMessage_ReturnsMessage()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent { Data = "test" });

        var result = fake.ShouldHavePublished<TestEvent>();

        result.Data.Should().Be("test");
    }

    [Test]
    public async Task ShouldHavePublished_WithPredicate_ReturnsMatchingMessage()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent { Data = "first" });
        await fake.PublishDirectAsync(new TestEvent { Data = "second" });

        var result = fake.ShouldHavePublished<TestEvent>(m => m.Data == "second");

        result.Data.Should().Be("second");
    }

    [Test]
    public async Task ShouldHavePublished_WithNoMessages_Throws()
    {
        var fake = new FakeRatatoskr();

        var act = () => fake.ShouldHavePublished<TestEvent>();

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*TestEvent*none were found*");
    }

    [Test]
    public async Task ShouldHavePublished_WithWrongType_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent { Data = "test" });

        var act = () => fake.ShouldHavePublished<OrderCreatedEvent>();

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*OrderCreatedEvent*none were found*TestEvent*");
    }

    [Test]
    public async Task ShouldHavePublished_WithNonMatchingPredicate_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent { Data = "test" });

        var act = () => fake.ShouldHavePublished<TestEvent>(m => m.Data == "other");

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*1 published message*none matched the predicate*");
    }

    [Test]
    public async Task ShouldNotHavePublished_WithNoMatchingType_Succeeds()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        fake.ShouldNotHavePublished<OrderCreatedEvent>();
    }

    [Test]
    public async Task ShouldNotHavePublished_WithMatchingType_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        var act = () => fake.ShouldNotHavePublished<TestEvent>();

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*no published messages*TestEvent*found 1*");
    }

    [Test]
    public async Task ShouldHavePublishedCount_WithCorrectCount_Succeeds()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());
        await fake.PublishDirectAsync(new TestEvent());

        fake.ShouldHavePublishedCount(2);
    }

    [Test]
    public async Task ShouldHavePublishedCount_WithIncorrectCount_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        var act = () => fake.ShouldHavePublishedCount(5);

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*5 published*found 1*");
    }

    [Test]
    public async Task ShouldHavePublishedCountOfType_WithCorrectCount_Succeeds()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());
        await fake.PublishDirectAsync(new OrderCreatedEvent());
        await fake.PublishDirectAsync(new TestEvent());

        fake.ShouldHavePublishedCount<TestEvent>(2);
    }

    [Test]
    public async Task ShouldHavePublishedCountOfType_WithIncorrectCount_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        var act = () => fake.ShouldHavePublishedCount<TestEvent>(3);

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*3 published*TestEvent*found 1*");
    }

    [Test]
    public void ShouldBeEmpty_WithNoMessages_Succeeds()
    {
        var fake = new FakeRatatoskr();

        fake.ShouldBeEmpty();
    }

    [Test]
    public async Task ShouldBeEmpty_WithMessages_Throws()
    {
        var fake = new FakeRatatoskr();
        await fake.PublishDirectAsync(new TestEvent());

        var act = () => fake.ShouldBeEmpty();

        act.Should().Throw<RatatoskrTestException>()
            .WithMessage("*no published messages*found 1*");
    }
}
