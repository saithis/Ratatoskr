# AI Agent Instructions for Ratatoskr Tests

## Testing Framework

This project uses **TUnit** as the test framework and **AwesomeAssertions** for assertions.

### Why AwesomeAssertions?

**AwesomeAssertions** is a fork of FluentAssertions that maintains an open-source friendly license. FluentAssertions changed to a license unsuitable for open-source projects, so we use AwesomeAssertions instead.

**Important for AI Agents**: AwesomeAssertions has the **EXACT SAME API** as FluentAssertions. You can use FluentAssertions documentation and examples directly.

**CRITICAL**: When searching for documentation or examples online, search for **"FluentAssertions"** not "AwesomeAssertions". The APIs are 99% identical - only the package name differs due to licensing.

### Assertion Syntax

Use AwesomeAssertions fluent syntax for all assertions:

```csharp
using AwesomeAssertions;

// Value assertions
result.Should().Be(expected);
result.Should().NotBe(unexpected);
result.Should().BeNull();
result.Should().NotBeNull();

// Numeric assertions
count.Should().Be(5);
amount.Should().BeGreaterThan(0);
amount.Should().BeLessThanOrEqualTo(100);

// String assertions
name.Should().Be("expected");
name.Should().Contain("substring");
name.Should().StartWith("prefix");

// Boolean assertions
isValid.Should().BeTrue();
isValid.Should().BeFalse();

// Collection assertions
list.Should().HaveCount(3);
list.Should().Contain(item);
list.Should().BeEmpty();
list.Should().NotBeEmpty();
dictionary.Should().ContainKey("key");

// Exception assertions
Action act = () => SomeMethod();
act.Should().Throw<InvalidOperationException>();
act.Should().Throw<InvalidOperationException>()
   .WithMessage("*expected message*");

// Async exception assertions
Func<Task> act = async () => await SomeAsyncMethod();
await act.Should().ThrowAsync<InvalidOperationException>();
```

### TUnit Test Syntax

```csharp
using TUnit.Core;

[Test]
public async Task TestName_Scenario_ExpectedBehavior()
{
    // Arrange
    var sut = new SystemUnderTest();

    // Act
    var result = await sut.DoSomethingAsync();

    // Assert
    result.Should().Be(expectedValue);
}
```

### TestContainers Usage

We use shared container fixtures for performance:

```csharp
[ClassDataSource<PostgresContainerFixture>(Shared = SharedType.PerTestSession)]
public class MyIntegrationTests(PostgresContainerFixture postgres)
{
    [Test]
    public async Task MyTest()
    {
        // Use postgres.ConnectionString
    }
}
```

### Common Patterns

#### Integration Testing with RabbitMQ and PostgreSQL

All integration tests extend `RatatoskrIntegrationTest` which provides shared RabbitMQ and PostgreSQL containers, per-test database isolation, and helper methods:

```csharp
public class MyTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    [Test]
    public async Task MyTest()
    {
        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel("my-exchange", c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>());
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.UseRatatoskr<TestDbContext>(sp);
            });
        });

        // Use InScopeAsync, GetMessageAsync, WaitForConditionAsync etc.
    }
}
```

#### Testing with FakeTimeProvider

```csharp
using Microsoft.Extensions.Time.Testing;

var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
// ... use in services

// Advance time
fakeTime.Advance(TimeSpan.FromSeconds(5));
```

#### Testing Outbox with Manual Processing

For tests that need fine-grained control (retry/poison scenarios), disable the background
processor and process batches explicitly:

```csharp
bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox(o => o.WithoutBackgroundProcessing()));

// later, inside a scope:
var processor = scope.ServiceProvider.GetRequiredService<OutboxMessageProcessor<TestDbContext>>();
await processor.ProcessBatchAsync(includeStuckMessageDetection: false, CancellationToken.None);
```

## DO NOT

- ❌ Use TUnit assertions (`await Assert.That(...)`) - use AwesomeAssertions instead
- ❌ Reference FluentAssertions package - use AwesomeAssertions
- ❌ Use xUnit, NUnit, or MSTest syntax - use TUnit
- ❌ Create tests that depend on execution order - make them independent
- ❌ Share mutable state between tests - use fixtures for shared resources

## DO

- ✅ Use AwesomeAssertions for all assertions (same API as FluentAssertions)
- ✅ Use TUnit `[Test]` attribute for test methods
- ✅ Make tests independent and parallel-safe
- ✅ Use descriptive test names: `Method_Scenario_ExpectedBehavior`
- ✅ Use shared container fixtures with `SharedType.PerTestSession`
- ✅ Clean up resources properly (use `IAsyncDisposable` for fixtures)
- ✅ Use `FakeTimeProvider` for time-dependent tests
- ✅ Prefer integration tests with real RabbitMQ and PostgreSQL over unit tests with mocks

## Example Integration Test

```csharp
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.Tests.Fixtures;
using TUnit.Core;

namespace Ratatoskr.Tests.Integration;

public class ExampleTests(RabbitMqContainerFixture rabbitMq, PostgresContainerFixture postgres)
    : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    [Test]
    public async Task Outbox_MessagePublished_DeliveredToQueue()
    {
        // Arrange
        var exchangeName = $"example-{TestId}";
        var queueName = $"example-queue-{TestId}";

        await StartTestAsync(services =>
        {
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(o => o.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddEventPublishChannel(exchangeName, c => c
                    .WithRabbitMq(r => r.WithTopicExchange())
                    .Produces<TestEvent>(m => m.WithRoutingKey("test.event")));
                bus.AddEfCoreDurability<TestDbContext>(d => d.UseOutbox());
            });

            services.AddDbContext<TestDbContext>((sp, options) =>
            {
                options.UseNpgsql(PostgresConnectionString);
                options.UseRatatoskr<TestDbContext>(sp);
            });
        });

        await EnsureQueueBoundAsync(queueName, exchangeName, "test.event");

        // Initialize database
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
        });

        // Act - Stage message via outbox
        await InScopeAsync(async ctx =>
        {
            var dbContext = ctx.ServiceProvider.GetRequiredService<TestDbContext>();
            dbContext.Outbox.Add(new TestEvent { Data = "hello" });
            await dbContext.SaveChangesAsync();
        });

        // Assert - Wait for message in queue
        await WaitForConditionAsync(
            async () => await GetMessageCountAsync(queueName) >= 1,
            TimeSpan.FromSeconds(10));
    }
}
```

## Running Tests

See "Running tests" in the root `AGENTS.md`. In short, from the repository root:

```bash
# Run all tests
dotnet run --project tests/Ratatoskr.Tests -- --maximum-parallel-tests 10

# Run a specific test class (TUnit has no --filter; use --treenode-filter)
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/ExampleTests/*"

# Run with coverage
dotnet run --project tests/Ratatoskr.Tests -- --coverage
```
