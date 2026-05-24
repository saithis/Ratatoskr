using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.EfCore;

public class DatabaseProviderHelperTests
{
    [Test]
    public void GetOutboxProcessingFilter_InMemory_ReturnsNull()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new TestDbContext(options);

        DatabaseProviderHelper.GetOutboxProcessingFilter(context.Database).Should().BeNull();
    }

    [Test]
    public void GetInboxProcessingFilter_InMemory_ReturnsNull()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new TestDbContext(options);

        DatabaseProviderHelper.GetInboxProcessingFilter(context.Database).Should().BeNull();
    }

    [Test]
    public void AddRatatoskrEfCoreModel_PostgreSql_ModelIncludesProcessingIndexFilters()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost")
            .Options;
        using var context = new TestDbContext(options);

        var outbox = context.Model.FindEntityType(typeof(OutboxMessageEntity));
        outbox.Should().NotBeNull();
        var outboxIndex = outbox.FindIndex("IX_OutboxMessages_Processing");
        outboxIndex.Should().NotBeNull();
        outboxIndex.GetFilter().Should().Be("\"ProcessedAt\" IS NULL AND \"IsPoisoned\" = false");

        var handlerStatus = context.Model.FindEntityType(typeof(InboxHandlerStatusEntity));
        handlerStatus.Should().NotBeNull();
        var inboxIndex = handlerStatus.FindIndex("IX_InboxHandlerStatuses_Processing");
        inboxIndex.Should().NotBeNull();
        inboxIndex.GetFilter().Should().Be("\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false");
    }

    [Test]
    public void GetOutboxProcessingFilter_PostgreSQL_ReturnsCorrectFilter()
    {
        // UseNpgsql sets the provider name without requiring a real connection
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost")
            .Options;
        using var context = new TestDbContext(options);

        var filter = DatabaseProviderHelper.GetOutboxProcessingFilter(context.Database);

        filter.Should().Be("\"ProcessedAt\" IS NULL AND \"IsPoisoned\" = false");
    }

    [Test]
    public void GetInboxProcessingFilter_PostgreSQL_ReturnsCorrectFilter()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost")
            .Options;
        using var context = new TestDbContext(options);

        var filter = DatabaseProviderHelper.GetInboxProcessingFilter(context.Database);

        filter.Should().Be("\"CompletedAt\" IS NULL AND \"IsPoisoned\" = false");
    }
}
