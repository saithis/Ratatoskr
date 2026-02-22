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

#### Testing with MessageSink / RatatoskrTestHarness

```csharp
var harness = provider.GetRequiredService<RatatoskrTestHarness>();

// Assert message was sent (returns typed SentMessage<T>)
var sent = harness.Sent.ShouldContain<MyEvent>(e => e.Id == "123");
sent.Properties.Type.Should().Be("expected.type");

// Or use raw messages
harness.Sent.Messages.Should().HaveCount(1);
```

#### Testing with FakeTimeProvider

```csharp
using Microsoft.Extensions.Time.Testing;

var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
// ... use in services

// Advance time
fakeTime.Advance(TimeSpan.FromSeconds(5));
```

#### Testing Outbox Processing

```csharp
using (var scope = provider.CreateScope())
{
    var processor = scope.ServiceProvider.GetRequiredService<SynchronousOutboxProcessor<TestDbContext>>();
    var processedCount = await processor.ProcessAllAsync();
    
    processedCount.Should().Be(expectedCount);
}
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
- ✅ Use `RatatoskrTestHarness` / `MessageSink` and `OutboxTestHelper` for testing

## Example Test

```csharp
using AwesomeAssertions;
using Ratatoskr.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Ratatoskr;
using Ratatoskr.Testing;
using TUnit.Core;

namespace Ratatoskr.Tests.Core;

public class ExampleTests
{
    [Test]
    public async Task PublishDirectAsync_WithMessage_SendsCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTestRatatoskr(bus => bus
            .AddMessage<TestEvent>("test.event"));
        
        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IRatatoskr>();
        var harness = provider.GetRequiredService<RatatoskrTestHarness>();

        // Act
        await bus.PublishDirectAsync(new TestEvent { Data = "test" });

        // Assert
        var sent = harness.Sent.ShouldContain<TestEvent>(e => e.Data == "test");
        sent.Properties.Type.Should().Be("test.event");
    }
}
```

## Running Tests

```bash
# Build tests
dotnet build

# Run all tests
dotnet run

# Run with coverage
dotnet run --coverage

# Run specific test class
dotnet run --filter "ClassName~ExampleTests"
```
