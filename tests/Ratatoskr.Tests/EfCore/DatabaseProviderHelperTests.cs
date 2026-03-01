using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Ratatoskr.EfCore.Internal;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.EfCore;

public class DatabaseProviderHelperTests
{
    [Test]
    public void GetOutboxProcessingFilter_NullDatabase_ReturnsNull()
    {
        DatabaseProviderHelper.GetOutboxProcessingFilter(null).Should().BeNull();
    }

    [Test]
    public void GetInboxProcessingFilter_NullDatabase_ReturnsNull()
    {
        DatabaseProviderHelper.GetInboxProcessingFilter(null).Should().BeNull();
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
