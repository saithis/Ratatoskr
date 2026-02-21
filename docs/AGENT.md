# Documentation Guidelines

This directory contains the user-facing documentation for Ratatoskr.

## Structure

- **`overview.md`**: High-level introduction and goals.
- **`configuration.md`**: Detailed configuration options.
- **`roadmap.md`**: Future plans and tasks.
- **`topology.md`**: The RabbitMQ retry topology.

## Maintenance Rules

1. **Keep in Sync**: When changing code that affects configuration or public APIs, **always** check `docs/` and update the relevant files.
2. **Clear Examples**: Use C# code blocks for configuration examples.
3. **Diagrams**: If adding diagrams, use Mermaid syntax within markdown blocks.

## Tone

- Technical but accessible.
- Focus on "Developer Experience" (DX) as stated in the project goals.
