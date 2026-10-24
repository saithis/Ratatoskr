# AI Agent Instructions

This is the single source of instructions for AI coding agents working in this repository.
`CLAUDE.md` and `GEMINI.md` are symlinks to this file (on Windows, git needs symlink support
enabled to check them out as links). Detailed testing guidance lives in
`tests/Ratatoskr.Tests/AGENTS.md`.

## Approach

- Read existing files before writing. Don't re-read unless changed.
- Thorough in reasoning, concise in output.
- Skip files over 100KB unless required.
- No sycophantic openers or closing fluff.
- No emojis or em-dashes.
- Do not guess APIs, versions, flags, commit SHAs, or package names. Verify by reading code or docs before asserting.

## General rules

- For every code change, make sure it is covered by tests
- Prefer integration tests over unit tests
- Use AwesomeAssertions for assertions
- Always make sure the documentation in `docs/` is in sync
- Always use TimeProvider to create instances of DateTime/DateTimeOffset

## Project structure

- `src/Ratatoskr`: the core library, including the EF Core outbox/inbox (`EfCore/`) and its management API.
- `src/Ratatoskr.RabbitMq`: the RabbitMQ transport.
- `src/Ratatoskr.Testing`: helpers for testing applications that use Ratatoskr.
- `src/Ratatoskr.Management.*` and `src/Ratatoskr.UI`: the management API abstractions and UI.
- `examples/`: an Aspire-based example application.

## Coding standards

- Nullable reference types are enabled globally. Ensure null checks are appropriate.
- Use asynchronous patterns for all I/O bound operations.
- All services are designed for `Microsoft.Extensions.DependencyInjection`.
- Use `[LoggerMessage]` partial methods for logging.

## Testing

This project uses **TUnit** as the test framework and **AwesomeAssertions** for assertions.
AwesomeAssertions is a fork of FluentAssertions with an open-source friendly license. Except for
the using statement, the API is identical, so FluentAssertions documentation applies.

```csharp
result.Should().Be(expected);
collection.Should().HaveCount(5);
act.Should().Throw<InvalidOperationException>();
```

### Running tests

Run tests with `dotnet run`, not `dotnet test`.

```bash
# Run all tests (use --maximum-parallel-tests 10 to avoid inotify limit issues)
dotnet run --project tests/Ratatoskr.Tests -- --maximum-parallel-tests 10

# Run a single test by name (treenode-filter format: /*/*/ClassName/TestName)
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/InboxTests/Inbox_MyTestName"

# Run all tests in a class
dotnet run --project tests/Ratatoskr.Tests -- --treenode-filter "/*/*/OutboxBasicTests/*" --maximum-parallel-tests 10

# List all tests
dotnet run --project tests/Ratatoskr.Tests -- --list-tests
```

### TUnit filter tips

- The `--treenode-filter` uses a path format: `/*/*/ClassName/TestName` (assembly/namespace/class/test).
- Use `*` wildcards for segments you don't want to match exactly.
- Wildcard patterns like `*OutboxTests*` do NOT work; use the full path format `/*/*/OutboxBasicTests/*`.
- Use `|` with parentheses to match one of several values in a segment: `/*/*/(Class1)|(Class2)/*`.
- To find the exact class name for a test, use `--list-tests` and look at the grouping.
- Do NOT use `--filter` (MSTest/xUnit style); TUnit only supports `--treenode-filter`.

## Aspire

The example application is orchestrated with Aspire; the resources are defined in the app host
under `examples/`.

- Before making changes to the example application, run it with `aspire run` and inspect the
  state of the resources so you start from a known state. Changes to the app host require a restart.
- Use the Aspire MCP tools (list resources, structured logs, console logs, traces) to debug
  issues before making changes.
- When adding an integration, use the list integrations tool to pick the version that matches
  the `Aspire.AppHost.Sdk`, and read its docs with the get integration docs tool.
- Update the app host with `aspire update`. The Aspire workload is obsolete; never install or use it.
- Avoid persistent containers during development to avoid state management issues on restart.
- Prefer the official documentation: https://aspire.dev, https://learn.microsoft.com/dotnet/aspire
  and https://nuget.org.
