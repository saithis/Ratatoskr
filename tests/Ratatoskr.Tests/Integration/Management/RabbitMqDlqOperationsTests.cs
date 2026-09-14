using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Ratatoskr.Core;
using Ratatoskr.Management.Contracts;
using Ratatoskr.RabbitMq.Config;
using Ratatoskr.RabbitMq.Extensions;
using Ratatoskr.RabbitMq.Management;
using Ratatoskr.Tests.Fixtures;

namespace Ratatoskr.Tests.Integration.Management;

public class RabbitMqDlqOperationsTests(
    RabbitMqContainerFixture rabbitMq,
    PostgresContainerFixture postgres
) : RatatoskrIntegrationTest(rabbitMq, postgres)
{
    private string ChannelName => $"dlq.test.channel.{TestId}";
    private string QueueName => $"dlq.test.queue.{TestId}";
    private string DlqName => $"{QueueName}.dlq";

    private async Task StartDlqTestHostAsync()
    {
        await StartTestAsync(services =>
        {
            services.AddSingleton<TestEventHandler>();
            services.AddRatatoskr(bus =>
            {
                bus.UseRabbitMq(c => c.ConnectionString = new Uri(RabbitMqConnectionString));
                bus.AddCommandConsumeChannel(
                    ChannelName,
                    c => c.WithRabbitMq(r => r.WithDirectExchange().WithQueueName(QueueName))
                          .Consumes<TestEvent>(m => m.WithHandler<TestEventHandler>())
                );
            });
        });
    }

    private async Task<IConnection> CreateAmqpConnectionAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        return await factory.CreateConnectionAsync();
    }

    private async Task PublishToDlqDirectAsync(int count)
    {
        await using var connection = await CreateAmqpConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        for (var i = 0; i < count; i++)
        {
            var props = new BasicProperties
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ContentType = "application/json",
                Type = "test.event",
                Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x-death"] = "some-dead-letter-info",
                    ["x-original-queue"] = QueueName,
                },
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Index = i }));
            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: DlqName,
                mandatory: false,
                basicProperties: props,
                body: body
            );
        }
    }

    [Test]
    public async Task QueueResolver_ReportsQueueAndDlqDepth()
    {
        await StartDlqTestHostAsync();
        await PublishToDlqDirectAsync(3);

        await InScopeAsync(async ctx =>
        {
            var resolver = ctx.ServiceProvider.GetRequiredService<IChannelQueueResolver>();
            var channelRegistry = ctx.ServiceProvider.GetRequiredService<ChannelRegistry>();
            var queues = await resolver.ResolveQueuesAsync(
                ChannelName,
                ChannelIntent.Consume,
                ["test.event"],
                default
            );

            queues.Should().HaveCount(1);
            var q = queues[0];
            q.QueueName.Should().Be(QueueName);
            q.DeadLetterQueueName.Should().Be(DlqName);
            q.DeadLetterCount.Should().Be(3);
        });
    }

    [Test]
    public async Task DlqRequeue_RequeuesBatchAndPreservesHeaders()
    {
        await StartDlqTestHostAsync();
        await PublishToDlqDirectAsync(5);

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var requeueOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqRequeue);
            requeueOp.Should().NotBeNull();

            // Requeue first 2 messages
            var req1 = new DlqRequeueRequest(ChannelName, QueueName, Limit: 2);
            var opCtx1 = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqRequeue,
                Request = req1,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res1 = await requeueOp!.ExecuteAsync(opCtx1, default);

            res1.Status.Should().Be(ManagementResultStatus.Ok);
            var resp1 = (DlqRequeueResponse)res1.Value!;
            resp1.RequeuedCount.Should().Be(2);
            resp1.RemainingCount.Should().Be(3);

            // Requeue remaining messages (all)
            var req2 = new DlqRequeueRequest(ChannelName, QueueName, Limit: null);
            var opCtx2 = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqRequeue,
                Request = req2,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res2 = await requeueOp.ExecuteAsync(opCtx2, default);

            res2.Status.Should().Be(ManagementResultStatus.Ok);
            var resp2 = (DlqRequeueResponse)res2.Value!;
            resp2.RequeuedCount.Should().Be(3);
            resp2.RemainingCount.Should().Be(0);
        });

        // Verify DLQ is empty now
        await using var connection = await CreateAmqpConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var dlqCount = await channel.MessageCountAsync(DlqName);
        dlqCount.Should().Be(0);
    }

    [Test]
    public async Task DlqPurge_PurgesAllMessages()
    {
        await StartDlqTestHostAsync();
        await PublishToDlqDirectAsync(4);

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var purgeOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqPurge);
            purgeOp.Should().NotBeNull();

            var req = new DlqPurgeRequest(ChannelName, QueueName);
            var opCtx = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqPurge,
                Request = req,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res = await purgeOp!.ExecuteAsync(opCtx, default);

            res.Status.Should().Be(ManagementResultStatus.Ok);
            var resp = (DlqPurgeResponse)res.Value!;
            resp.PurgedCount.Should().Be(4);
        });

        // Verify DLQ is now 0
        await using var connection = await CreateAmqpConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var dlqCount = await channel.MessageCountAsync(DlqName);
        dlqCount.Should().Be(0);
    }

    [Test]
    public async Task DlqRequeue_UnknownChannel_ReturnsChannelNotFound()
    {
        await StartDlqTestHostAsync();

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var requeueOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqRequeue);
            requeueOp.Should().NotBeNull();

            var req = new DlqRequeueRequest("nonexistent.channel", QueueName, Limit: 1);
            var opCtx = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqRequeue,
                Request = req,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res = await requeueOp!.ExecuteAsync(opCtx, default);

            res.Status.Should().Be(ManagementResultStatus.NotFound);
            res.Error.Should().NotBeNull();
            res.Error!.Code.Should().Be("channel_not_found");
            res.Error.Detail.Should().Contain("nonexistent.channel");
        });
    }

    [Test]
    public async Task DlqPurge_UnknownChannel_ReturnsChannelNotFound()
    {
        await StartDlqTestHostAsync();

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var purgeOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqPurge);
            purgeOp.Should().NotBeNull();

            var req = new DlqPurgeRequest("nonexistent.channel", QueueName);
            var opCtx = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqPurge,
                Request = req,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res = await purgeOp!.ExecuteAsync(opCtx, default);

            res.Status.Should().Be(ManagementResultStatus.NotFound);
            res.Error.Should().NotBeNull();
            res.Error!.Code.Should().Be("channel_not_found");
            res.Error.Detail.Should().Contain("nonexistent.channel");
        });
    }

    [Test]
    public async Task DlqRequeue_NonexistentDlq_ReturnsDlqNotFound()
    {
        await StartDlqTestHostAsync();

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var requeueOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqRequeue);
            requeueOp.Should().NotBeNull();

            var req = new DlqRequeueRequest(ChannelName, "nonexistent.queue", Limit: 1);
            var opCtx = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqRequeue,
                Request = req,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res = await requeueOp!.ExecuteAsync(opCtx, default);

            res.Status.Should().Be(ManagementResultStatus.NotFound);
            res.Error.Should().NotBeNull();
            res.Error!.Code.Should().Be("dlq_not_found");
            res.Error.Detail.Should().Contain("nonexistent.queue.dlq");
        });
    }

    [Test]
    public async Task DlqPurge_NonexistentDlq_ReturnsDlqNotFound()
    {
        await StartDlqTestHostAsync();

        await InScopeAsync(async ctx =>
        {
            var operations = ctx.ServiceProvider.GetServices<IManagementOperation>();
            var purgeOp = operations.FirstOrDefault(o => o.Name == ManagementOperationNames.DlqPurge);
            purgeOp.Should().NotBeNull();

            var req = new DlqPurgeRequest(ChannelName, "nonexistent.queue");
            var opCtx = new ManagementOperationContext
            {
                Operation = ManagementOperationNames.DlqPurge,
                Request = req,
                OperationId = Guid.NewGuid(),
                Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            };
            var res = await purgeOp!.ExecuteAsync(opCtx, default);

            res.Status.Should().Be(ManagementResultStatus.NotFound);
            res.Error.Should().NotBeNull();
            res.Error!.Code.Should().Be("dlq_not_found");
            res.Error.Detail.Should().Contain("nonexistent.queue.dlq");
        });
    }
}

