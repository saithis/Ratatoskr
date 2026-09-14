using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PlaygroundHost.Persistence;

public sealed class PublisherDbContextFactory : IDesignTimeDbContextFactory<PublisherDbContext>
{
    public PublisherDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<PublisherDbContext>()
                .UseNpgsql("Host=localhost;Database=publisherdb")
                .Options
        );
}

public sealed class ConsumerDbContextFactory : IDesignTimeDbContextFactory<ConsumerDbContext>
{
    public ConsumerDbContext CreateDbContext(string[] args) =>
        new(
            new DbContextOptionsBuilder<ConsumerDbContext>()
                .UseNpgsql("Host=localhost;Database=consumerdb")
                .Options
        );
}
