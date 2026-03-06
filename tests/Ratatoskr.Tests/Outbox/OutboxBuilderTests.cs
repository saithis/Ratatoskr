using AwesomeAssertions;
using Ratatoskr.EfCore;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Outbox;

public class OutboxBuilderTests
{
    private static OutboxBuilder<TestDbContext> CreateBuilder() =>
        new();

    [Test]
    public void DefaultOptions_HaveSensibleDefaults()
    {
        // Arrange
        var builder = CreateBuilder();

        // Assert
        builder.Options.PollingInterval.Should().Be(TimeSpan.FromSeconds(60));
        builder.Options.BatchSize.Should().Be(100);
        builder.Options.MaxRetries.Should().Be(5);
        builder.Options.RestartDelay.Should().Be(TimeSpan.FromSeconds(5));
        builder.Options.LockAcquireTimeout.Should().Be(TimeSpan.FromSeconds(60));
        builder.Options.StuckMessageThreshold.Should().Be(TimeSpan.FromMinutes(5));
        builder.Options.MaxRetryDelay.Should().Be(TimeSpan.FromMinutes(5));
        builder.Options.LockName.Should().Be("OutboxProcessor");
        builder.RegisterBackgroundService.Should().BeTrue();
    }

    [Test]
    public void WithBatchSize_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithBatchSize(50);

        // Assert
        builder.Options.BatchSize.Should().Be(50);
    }

    [Test]
    public void WithBatchSize_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var actZero = () => builder.WithBatchSize(0);
        var actNegative = () => builder.WithBatchSize(-1);

        // Assert
        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithMaxRetries_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithMaxRetries(10);

        // Assert
        builder.Options.MaxRetries.Should().Be(10);
    }

    [Test]
    public void WithMaxRetries_Zero_IsAllowed()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithMaxRetries(0);

        // Assert
        builder.Options.MaxRetries.Should().Be(0);
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

    [Test]
    public void WithPollingInterval_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithPollingInterval(TimeSpan.FromSeconds(30));

        // Assert
        builder.Options.PollingInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void WithPollingInterval_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithPollingInterval(TimeSpan.Zero);
        var actNegative = () => builder.WithPollingInterval(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithMaxRetryDelay_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithMaxRetryDelay(TimeSpan.FromMinutes(10));

        // Assert
        builder.Options.MaxRetryDelay.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Test]
    public void WithMaxRetryDelay_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithMaxRetryDelay(TimeSpan.Zero);
        var actNegative = () => builder.WithMaxRetryDelay(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithStuckMessageThreshold_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithStuckMessageThreshold(TimeSpan.FromMinutes(10));

        // Assert
        builder.Options.StuckMessageThreshold.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Test]
    public void WithStuckMessageThreshold_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithStuckMessageThreshold(TimeSpan.Zero);
        var actNegative = () => builder.WithStuckMessageThreshold(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithRestartDelay_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithRestartDelay(TimeSpan.FromSeconds(10));

        // Assert
        builder.Options.RestartDelay.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void WithRestartDelay_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithRestartDelay(TimeSpan.Zero);
        var actNegative = () => builder.WithRestartDelay(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithLockAcquireTimeout_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithLockAcquireTimeout(TimeSpan.FromSeconds(120));

        // Assert
        builder.Options.LockAcquireTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Test]
    public void WithLockAcquireTimeout_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithLockAcquireTimeout(TimeSpan.Zero);
        var actNegative = () => builder.WithLockAcquireTimeout(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithLockName_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithLockName("CustomLock");

        // Assert
        builder.Options.LockName.Should().Be("CustomLock");
    }

    [Test]
    public void WithLockName_NullOrWhitespace_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actNull = () => builder.WithLockName(null!);
        var actEmpty = () => builder.WithLockName("");
        var actWhitespace = () => builder.WithLockName("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithSendTimeout_SetsOption()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithSendTimeout(TimeSpan.FromSeconds(30));

        // Assert
        builder.Options.SendTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void WithSendTimeout_ZeroOrNegative_Throws()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act & Assert
        var actZero = () => builder.WithSendTimeout(TimeSpan.Zero);
        var actNegative = () => builder.WithSendTimeout(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNegative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithoutBackgroundProcessing_DisablesHostedService()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.WithoutBackgroundProcessing();

        // Assert
        builder.RegisterBackgroundService.Should().BeFalse();
    }

    [Test]
    public void Configure_AppliesAllOptions()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        builder.Configure(opts =>
        {
            opts.BatchSize = 25;
            opts.MaxRetries = 3;
            opts.PollingInterval = TimeSpan.FromSeconds(10);
            opts.LockName = "CustomLock";
        });

        // Assert
        builder.Options.BatchSize.Should().Be(25);
        builder.Options.MaxRetries.Should().Be(3);
        builder.Options.PollingInterval.Should().Be(TimeSpan.FromSeconds(10));
        builder.Options.LockName.Should().Be("CustomLock");
    }

    [Test]
    public void FluentApi_ReturnsSameBuilderInstance()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var result = builder
            .WithBatchSize(50)
            .WithMaxRetries(3)
            .WithPollingInterval(TimeSpan.FromSeconds(10))
            .WithMaxRetryDelay(TimeSpan.FromMinutes(1))
            .WithStuckMessageThreshold(TimeSpan.FromMinutes(10))
            .WithRestartDelay(TimeSpan.FromSeconds(3))
            .WithLockAcquireTimeout(TimeSpan.FromSeconds(30))
            .WithLockName("TestLock")
            .WithSendTimeout(TimeSpan.FromSeconds(5));

        // Assert
        result.Should().BeSameAs(builder);
    }
}
