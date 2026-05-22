using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

public class NotificationsWebhook
{
    private readonly ILogger<NotificationsWebhook> _logger;
    private readonly TableClient _tableClient;
    private readonly ILifecycleNotificationService _lifecycleNotificationService;
    private readonly IGraphSubscriptionManager _subscriptionManager;
    private readonly GraphSubscriptionSettings _subscriptionSettings;
    private readonly SubscriptionAdministrationWeb _subscriptionAdministrationWeb;
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string? _expectedClientState = Environment.GetEnvironmentVariable("GraphClientState");

    public NotificationsWebhook(
        ILogger<NotificationsWebhook> logger,
        ILifecycleNotificationService lifecycleNotificationService,
        IGraphSubscriptionManager subscriptionManager,
        GraphSubscriptionSettings subscriptionSettings)
    {
        _logger = logger;
        _lifecycleNotificationService = lifecycleNotificationService;
        _subscriptionManager = subscriptionManager;
        _subscriptionSettings = subscriptionSettings;
        _tableClient = CreateTableClient();
        _subscriptionAdministrationWeb = new SubscriptionAdministrationWeb(_logger, _subscriptionManager, _subscriptionSettings, _tableClient);
    }

    [Function("GraphNotifications")]
    public async Task<IActionResult> HandleNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/notifications")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        if (TryHandleValidation(req, out var validationResponse))
        {
            return validationResponse;
        }

        var payload = await DeserializePayloadAsync(req);
        if (payload?.Value == null || payload.Value.Length == 0)
        {
            _logger.LogWarning("Notification payload missing or empty.");
            return new BadRequestResult();
        }

        foreach (var notification in payload.Value)
        {
            if (!IsClientStateValid(notification.ClientState))
            {
                _logger.LogWarning("Client state mismatch for subscription {SubscriptionId}.", notification.SubscriptionId);
                continue;
            }

            _logger.LogInformation("Notification {ChangeType} for resource {Resource} (subscription {SubscriptionId}).",
                notification.ChangeType,
                notification.Resource,
                notification.SubscriptionId);

            await StoreNotificationAsync(notification, "change", cancellationToken);
        }

        return new AcceptedResult();
    }

    [Function("GraphNotificationsManagement")]
    public async Task<IActionResult> HandleManagementPage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/manage")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        return await _subscriptionAdministrationWeb
            .HandleManagementRequestAsync(req, cancellationToken)
            .ConfigureAwait(false);
    }

    [Function("GraphLifecycleNotifications")]
    public async Task<IActionResult> HandleLifecycleNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/lifecycle")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        if (TryHandleValidation(req, out var validationResponse))
        {
            return validationResponse;
        }

        var payload = await DeserializePayloadAsync(req);
        if (payload?.Value == null || payload.Value.Length == 0)
        {
            _logger.LogWarning("Lifecycle payload missing or empty.");
            return new BadRequestResult();
        }

        foreach (var notification in payload.Value)
        {
            _logger.LogInformation("Lifecycle event {LifecycleEvent} for subscription {SubscriptionId} expiring {Expiration}.",
                notification.LifecycleEvent,
                notification.SubscriptionId,
                notification.SubscriptionExpirationDateTime);

            await StoreNotificationAsync(notification, "lifecycle", cancellationToken);
            await _lifecycleNotificationService.ProcessAsync(notification, cancellationToken);
        }

        return new AcceptedResult();
    }

    private static TableClient CreateTableClient()
    {
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("AzureWebJobsStorage is not configured.");
        }

        var tableName = Environment.GetEnvironmentVariable("GraphNotificationsTableName");
        if (string.IsNullOrWhiteSpace(tableName))
        {
            tableName = "GraphNotifications";
        }

        var client = new TableClient(connectionString, tableName);
        client.CreateIfNotExists();
        return client;
    }

    private static bool TryHandleValidation(HttpRequest request, out IActionResult response)
    {
        var validationToken = request.Query["validationToken"].FirstOrDefault();
        if (!string.IsNullOrEmpty(validationToken))
        {
            response = new ContentResult
            {
                Content = validationToken,
                ContentType = "text/plain",
                StatusCode = StatusCodes.Status200OK
            };
            return true;
        }

        response = null!;
        return false;
    }

    private static async Task<GraphNotificationEnvelope?> DeserializePayloadAsync(HttpRequest req)
    {
        if (req.Body.CanSeek)
        {
            req.Body.Position = 0;
        }
        using var reader = new StreamReader(req.Body);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return JsonSerializer.Deserialize<GraphNotificationEnvelope>(body, _serializerOptions);
    }

    private static bool IsClientStateValid(string? clientState)
    {
        if (string.IsNullOrEmpty(_expectedClientState))
        {
            return true;
        }

        return string.Equals(_expectedClientState, clientState, StringComparison.Ordinal);
    }

    private async Task StoreNotificationAsync(GraphNotification notification, string category, CancellationToken cancellationToken)
    {
        var partitionKey = notification.SubscriptionId ?? "unknown";
        var rowKeyBase = notification.Id;
        if (string.IsNullOrEmpty(rowKeyBase))
        {
            rowKeyBase = Guid.NewGuid().ToString("n");
        }

        var entity = new NotificationEntity
        {
            PartitionKey = partitionKey,
            RowKey = string.Concat(category, "-", rowKeyBase),
            Category = category,
            ChangeType = notification.ChangeType,
            LifecycleEvent = notification.LifecycleEvent,
            Resource = notification.Resource,
            SubscriptionExpirationDateTime = notification.SubscriptionExpirationDateTime,
            ReceivedUtc = DateTimeOffset.UtcNow,
            ResourceDataJson = GetResourceDataJson(notification.ResourceData)
        };

        await _tableClient.UpsertEntityAsync(entity, cancellationToken: cancellationToken);
    }

    private static string? GetResourceDataJson(JsonElement resourceData)
    {
        return resourceData.ValueKind == JsonValueKind.Undefined || resourceData.ValueKind == JsonValueKind.Null
            ? null
            : resourceData.GetRawText();
    }
    internal sealed class NotificationEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!;
        public string RowKey { get; set; } = default!;
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
        public string? Category { get; set; }
        public string? ChangeType { get; set; }
        public string? LifecycleEvent { get; set; }
        public string? Resource { get; set; }
        public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }
        public DateTimeOffset ReceivedUtc { get; set; }
        public string? ResourceDataJson { get; set; }
    }
}