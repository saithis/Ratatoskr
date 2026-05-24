using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ratatoskr.AsyncApi.Attributes;
using Ratatoskr.AsyncApi.Config;
using Ratatoskr.AsyncApi.Extensions;
using Ratatoskr.AsyncApi.Model;
using Ratatoskr.AsyncApi.Schema;
using Ratatoskr.CloudEvents;
using Ratatoskr.Core;

namespace Ratatoskr.AsyncApi.Generation;

/// <summary>
/// Generates an AsyncAPI v3 document from the Ratatoskr channel registry and configuration.
/// Transport-agnostic: transport-specific bindings are applied by registered <see cref="IAsyncApiTransportBindingProvider"/> implementations.
/// </summary>
public sealed class AsyncApiDocumentGenerator(
    AsyncApiOptions options,
    ChannelRegistry channelRegistry,
    CloudEventsOptions cloudEventsOptions,
    IEnumerable<IAsyncApiTransportBindingProvider> bindingProviders
)
{
    private readonly JsonSchemaGenerator _schemaGenerator = new();

    [GeneratedRegex(@"(?<=[a-z])([A-Z])|(?<=[A-Z])([A-Z][a-z])")]
    private static partial Regex SentenceCasePattern();

    public AsyncApiDocument Generate()
    {
        var schemas = new Dictionary<string, JsonSchema>();
        var messages = new Dictionary<string, AsyncApiMessage>();

        var document = new AsyncApiDocument
        {
            Info = options.Info,
            Components = new AsyncApiComponents { Schemas = schemas, Messages = messages },
        };

        var allChannels = channelRegistry.GetAllChannels().ToList();

        // Generate channels, operations, messages, and schemas
        foreach (var channel in allChannels)
        {
            BuildChannel(channel, document, schemas, messages);
        }

        // Let transport providers add server definitions and references
        // (must run after BuildChannel so document.Channels is populated)
        foreach (var provider in bindingProviders)
        {
            provider.ConfigureServers(document, allChannels);
        }

        // Clean up empty component collections
        if (document.Components!.Schemas?.Count == 0)
        {
            document.Components.Schemas = null;
        }
        if (document.Components!.Messages?.Count == 0)
        {
            document.Components.Messages = null;
        }
        if (document.Components!.Schemas == null && document.Components.Messages == null)
        {
            document.Components = null;
        }

        return document;
    }

    private void BuildChannel(
        ChannelRegistration channel,
        AsyncApiDocument document,
        Dictionary<string, JsonSchema> schemas,
        Dictionary<string, AsyncApiMessage> componentMessages
    )
    {
        var asyncApiChannel = new AsyncApiChannel
        {
            Address = channel.ChannelName,
            Messages = new Dictionary<string, AsyncApiReference>(),
        };

        var channelOpts = channel.GetAsyncApiChannelOptions();
        asyncApiChannel.Title = channelOpts?.Title;
        asyncApiChannel.Summary = channelOpts?.Summary;
        asyncApiChannel.Description = channelOpts?.Description;

        // Build message definitions and link them to the channel
        foreach (var msg in channel.Messages)
        {
            componentMessages[msg.MessageTypeName] = BuildMessage(msg, channel, schemas);
            asyncApiChannel.Messages[msg.MessageTypeName] = AsyncApiReference.ToComponentMessage(
                msg.MessageTypeName
            );
        }

        if (asyncApiChannel.Messages.Count == 0)
        {
            asyncApiChannel.Messages = null;
        }

        document.Channels[channel.ChannelName] = asyncApiChannel;

        // Build operations for this channel
        BuildOperations(channel, document);

        // Allow transport providers to add bindings and additional channels
        foreach (var provider in bindingProviders)
        {
            provider.ConfigureChannel(channel, document);
        }
    }

    private AsyncApiMessage BuildMessage(
        MessageRegistration msg,
        ChannelRegistration channel,
        Dictionary<string, JsonSchema> schemas
    )
    {
        var msgAttr = msg.MessageType.GetCustomAttribute<AsyncApiMessageAttribute>();
        var msgOpts = msg.GetAsyncApiMessageOptions();

        // Resolve metadata: attribute takes precedence over options, which takes precedence over defaults
        var title = msgAttr?.Title ?? msgOpts?.Title ?? SentenceCaseName(msg.MessageType.Name);
        var description = msgAttr?.Description ?? msgOpts?.Description;
        var version = msgAttr?.Version ?? msgOpts?.Version ?? "1.0.0";
        // Infer message type and role from channel intent
        var messageType = channel.Intent is ChannelType.CommandPublish or ChannelType.CommandConsume
            ? EventCatalogMessageType.Command
            : EventCatalogMessageType.Event;

        // Role defaults: publish channels → provider, consume channels → client
        var role = channel.Intent is ChannelType.EventPublish or ChannelType.CommandPublish
            ? EventCatalogRole.Provider
            : EventCatalogRole.Client;

        // Generate the payload schema for the CLR type
        var dataSchema = _schemaGenerator.GenerateAndRegister(msg.MessageType, schemas);

        JsonSchema payloadSchema;
        string contentType;

        if (cloudEventsOptions.ContentMode == CloudEventsContentMode.Structured)
        {
            payloadSchema = CloudEventsSchemaHelper.BuildStructuredModePayloadSchema(dataSchema);
            contentType = "application/cloudevents+json";
        }
        else
        {
            payloadSchema = dataSchema;
            contentType = "application/json";
        }

        var asyncApiMessage = new AsyncApiMessage
        {
            Name = msg.MessageTypeName,
            Title = title,
            Description = description,
            ContentType = contentType,
            Payload = payloadSchema,
        };

        // Add EventCatalog extension properties
        asyncApiMessage.Extensions = new Dictionary<string, JsonElement>
        {
            ["x-eventcatalog-message-type"] = JsonSerializer.SerializeToElement(
                messageType.ToString().ToLowerInvariant()
            ),
            ["x-eventcatalog-role"] = JsonSerializer.SerializeToElement(
                role.ToString().ToLowerInvariant()
            ),
            ["x-eventcatalog-message-version"] = JsonSerializer.SerializeToElement(version),
        };

        // Apply transport message bindings (including binary mode headers)
        foreach (var provider in bindingProviders)
        {
            provider.ConfigureMessage(msg, channel, asyncApiMessage);
        }

        return asyncApiMessage;
    }

    private void BuildOperations(ChannelRegistration channel, AsyncApiDocument document)
    {
        var channelOpts = channel.GetAsyncApiChannelOptions();

        if (channelOpts?.Operation != null)
        {
            BuildGroupedOperation(channel, channelOpts.Operation, document);
        }
        else
        {
            BuildPerMessageOperations(channel, document);
        }
    }

    private void BuildGroupedOperation(
        ChannelRegistration channel,
        AsyncApiOperationOptions opOpts,
        AsyncApiDocument document
    )
    {
        var action = GetAction(channel);
        var operationId = opOpts.Id ?? channel.ChannelName;

        var operation = new AsyncApiOperation
        {
            Action = action,
            Channel = AsyncApiReference.ToChannel(channel.ChannelName),
            Title = opOpts.Title,
            Summary = opOpts.Summary,
            Description = opOpts.Description,
            Tags = opOpts.Tags?.Select(t => new AsyncApiTag { Name = t }).ToList(),
        };

        if (channel.Messages.Count > 0)
        {
            operation.Messages =
            [
                .. channel.Messages.Select(m =>
                    AsyncApiReference.ToChannelMessage(channel.ChannelName, m.MessageTypeName)
                ),
            ];
        }

        AddOperation(operationId, operation, channel, document);
    }

    private void BuildPerMessageOperations(ChannelRegistration channel, AsyncApiDocument document)
    {
        var action = GetAction(channel);

        // Phase 1: Group messages by operationId (messages sharing an ID are merged)
        var groups =
            new Dictionary<
                string,
                (AsyncApiOperationOptions? Opts, List<MessageRegistration> Messages)
            >();

        foreach (var msg in channel.Messages)
        {
            var msgOpts = msg.GetAsyncApiMessageOptions();
            var opOpts = msgOpts?.Operation;
            var operationId = opOpts?.Id ?? (action + msg.MessageType.Name);

            if (groups.TryGetValue(operationId, out var group))
            {
                group.Messages.Add(msg);

                // Merge: keep first non-null opts
                if (opOpts != null && group.Opts == null)
                {
                    groups[operationId] = (opOpts, group.Messages);
                }
            }
            else
            {
                groups[operationId] = (opOpts, [msg]);
            }
        }

        // Phase 2: Create operations
        foreach (var (operationId, (opOpts, messages)) in groups)
        {
            var operation = new AsyncApiOperation
            {
                Action = action,
                Channel = AsyncApiReference.ToChannel(channel.ChannelName),
                Title = opOpts?.Title,
                Summary = opOpts?.Summary,
                Description = opOpts?.Description,
                Tags = opOpts?.Tags?.Select(t => new AsyncApiTag { Name = t }).ToList(),
                Messages =
                [
                    .. messages.Select(m =>
                        AsyncApiReference.ToChannelMessage(channel.ChannelName, m.MessageTypeName)
                    ),
                ],
            };

            AddOperation(operationId, operation, channel, document);
        }
    }

    private void AddOperation(
        string operationId,
        AsyncApiOperation operation,
        ChannelRegistration channel,
        AsyncApiDocument document
    )
    {
        if (!document.Operations.TryAdd(operationId, operation))
        {
            throw new InvalidOperationException(
                $"Duplicate AsyncAPI operationId '{operationId}'. "
                    + "Use WithOperation(o => o.WithId(\"...\")) to set a unique ID."
            );
        }

        foreach (var provider in bindingProviders)
        {
            provider.ConfigureOperation(channel, operation);
        }
    }

    private static string GetAction(ChannelRegistration channel) =>
        channel.Intent is ChannelType.EventPublish or ChannelType.CommandPublish
            ? "send"
            : "receive";

    private static string SentenceCaseName(string typeName) =>
        SentenceCasePattern().Replace(typeName, " $1$2").Trim();
}
