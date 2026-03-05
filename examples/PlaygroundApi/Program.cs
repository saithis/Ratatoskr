using System.Text.Json;
using Medallion.Threading;
using Medallion.Threading.FileSystem;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlaygroundApi;
using PlaygroundApi.Database;
using PlaygroundApi.Database.Entities;
using PlaygroundApi.Messages;
using Ratatoskr;
using Ratatoskr.AsyncApi.Extensions;
using Ratatoskr.Core;
using Ratatoskr.EfCore;
using Ratatoskr.RabbitMq.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults for Aspire
builder.AddServiceDefaults();

// https://github.com/madelson/DistributedLock
var lockFileDirectory = new DirectoryInfo(Environment.CurrentDirectory); // choose where the lock files will live
builder.Services.AddSingleton<IDistributedLockProvider>(_ => new FileDistributedSynchronizationProvider(lockFileDirectory));

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);


var rabbitMqConnectionString = builder.Configuration.GetConnectionString("rabbitmq");
builder.Services.AddRatatoskr(bus =>
{
    bus
        .UseRabbitMq(c =>
        {
            c.ConnectionString = new Uri(rabbitMqConnectionString);
        });
    
    bus.AddEfCoreOutbox<NotesDbContext>();
    
    bus
        .AddEventPublishChannel("events.topic", c => c
            .WithRabbitMq(r => r
                .WithTopicExchange())
            .Produces<NoteAddedEvent>()
            .Produces<NoteDto>()
            .Produces<FailEvent>());

    bus.AddEventConsumeChannel("events.topic", c =>
        c.WithRabbitMq(r => r
                .WithQueueName("events.subscriptions"))
            .Consumes<NoteDto>(m => m.WithHandler<NoteDtoHandler>())
            .Consumes<NoteAddedEvent>(m => m.WithHandler<NoteAddedEventHandler>())
            .Consumes<FailEvent>(m => m.WithHandler<FailEventHandler>()));

    bus.AddCommandConsumeChannel("commands.topic", c =>
        c.WithRabbitMq(r => r
                .WithTopicExchange()
                .WithQueueName("commands.subscriptions"))
            .Consumes<AddNoteCommand>(m => m.WithHandler<AddNoteCommandHandler>()));

    bus.AddCommandPublishChannel("commands.topic", c =>
        c.WithRabbitMq(r => r
                .WithDirectExchange())
            .Produces<AddNoteCommand>());
});

// Use PostgreSQL when connection string is available (Aspire), otherwise use InMemory
var dbConnectionString = builder.Configuration.GetConnectionString("notesdb");
if (!string.IsNullOrEmpty(dbConnectionString))
{
    // Use AddDbContext directly to get access to IServiceProvider for interceptor registration
    // This bypasses Aspire's AddNpgsqlDbContext but still uses the connection string from Aspire
    builder.Services.AddDbContext<NotesDbContext>((sp, options) =>
    {
        options.UseNpgsql(dbConnectionString);
        options.RegisterOutbox<NotesDbContext>(sp);
    });
}
else
{
    builder.Services.AddDbContext<NotesDbContext>((sp, c) => c
        .UseInMemoryDatabase("notesDb")
        .RegisterOutbox<NotesDbContext>(sp));
}

var app = builder.Build();

// Map default endpoints for Aspire
app.MapDefaultEndpoints();

// Apply migrations if using PostgreSQL
if (!string.IsNullOrEmpty(dbConnectionString))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
}

app.MapGet("/", () => "Hello World!");

// Fail consumption test
app.MapPost("/fail", async ([FromBody] FailEvent dto, [FromServices] IRatatoskr bus) =>
{
    await bus.PublishDirectAsync(dto);
    return TypedResults.Ok(dto);
});

// Direct publishing example - no transaction
app.MapPost("/send", async ([FromBody] NoteDto dto, [FromServices] IRatatoskr bus) =>
{
    // PublishDirectAsync - sends immediately, no outbox
    await bus.PublishDirectAsync(dto);
    return TypedResults.Ok(dto);
});

// Outbox publishing example - transactional
app.MapPost("/notes", async ([FromBody] NoteDto dto, [FromServices] NotesDbContext db) =>
{
    var note = new Note
    {
        Text = dto.Text,
        CreatedAt = DateTime.UtcNow,
    };
    
    // Both operations are transactional
    db.Notes.Add(note);
    db.OutboxMessages.Add(new NoteAddedEvent
    {
        Id = note.Id,
        Text = $"New Note: {dto.Text}",
    });
    
    await db.SaveChangesAsync(); // Note saved AND event queued atomically
    return TypedResults.Ok(note);
});
app.MapGet("/notes", async ([FromServices] NotesDbContext db) =>
{
    var notes = await db.Notes.ToListAsync();
    return TypedResults.Ok(notes);
});

app.MapAsyncApi();

app.Run();

namespace PlaygroundApi
{
    public record NoteDto(string Text);
    public record FailEvent(string Text);

    public class NoteDtoHandler(ILogger<NoteDtoHandler> logger) : IMessageHandler<NoteDto>
    {
        public Task HandleAsync(NoteDto message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("Received and handled: {EventType} {Body} {Props}", message, JsonSerializer.Serialize(message), JsonSerializer.Serialize(properties));
            return Task.CompletedTask;
        }
    }
    public class NoteAddedEventHandler(ILogger<NoteAddedEventHandler> logger) : IMessageHandler<NoteAddedEvent>
    {
        public Task HandleAsync(NoteAddedEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("Received and handled: {EventType} {Body} {Props}", message, JsonSerializer.Serialize(message), JsonSerializer.Serialize(properties));
            return Task.CompletedTask;
        }
    }
    public class FailEventHandler(ILogger<FailEventHandler> logger) : IMessageHandler<FailEvent>
    {
        public Task HandleAsync(FailEvent message, MessageProperties properties, CancellationToken cancellationToken)
        {
            logger.LogInformation("Received and will throw: {EventType} {Body} {Props}", message, JsonSerializer.Serialize(message), JsonSerializer.Serialize(properties));
            throw new NotImplementedException("this event will fail, so we can test retry strategy and dlx");
        }
    }

    public class AddNoteCommandHandler(NotesDbContext dbContext, TimeProvider timeProvider, ILogger<AddNoteCommandHandler> logger) : IMessageHandler<AddNoteCommand>
    {
        public async Task HandleAsync(AddNoteCommand message, MessageProperties properties, CancellationToken cancellationToken)
        {
            dbContext.Notes.Add(new Note
            {
                Text = message.Text,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Received and handled: {EventType} {Body} {Props}", message, JsonSerializer.Serialize(message), JsonSerializer.Serialize(properties));
        }
    }
}
