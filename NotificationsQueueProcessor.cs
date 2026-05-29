/*
 * By David Barrett, Microsoft Ltd. Use at your own risk.  No warranties are given.
 * 
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * */

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

/// <summary>
/// Processes change and lifecycle notification queue messages asynchronously,
/// decoupling the fast HTTP acknowledgement from the slower storage and Graph API calls.
/// </summary>
public sealed class NotificationsQueueProcessor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<NotificationsQueueProcessor> _logger;
    private readonly TableClient _tableClient;
    private readonly ILifecycleNotificationService _lifecycleNotificationService;

    public NotificationsQueueProcessor(
        ILogger<NotificationsQueueProcessor> logger,
        TableClient tableClient,
        ILifecycleNotificationService lifecycleNotificationService)
    {
        _logger = logger;
        _tableClient = tableClient;
        _lifecycleNotificationService = lifecycleNotificationService;
    }

    /// <summary>
    /// Reads a change notification from the queue, stores it in Table Storage,
    /// and performs any required downstream processing.
    /// </summary>
    [Function("ProcessChangeNotification")]
    public async Task ProcessChangeNotificationAsync(
        [QueueTrigger("graph-change-notifications")] QueueMessage queueMessage,
        CancellationToken cancellationToken)
    {
        var messageText = queueMessage.MessageText.ToString();
        var message = DeserializeMessage(messageText);
        if (message is null)
        {
            _logger.LogError("Discarding unparseable change notification queue message: {MessageText}", Truncate(messageText, 256));
            return;
        }

        var notification = message.Notification;
        _logger.LogInformation(
            "Processing queued change notification {ChangeType} for resource {Resource} (subscription {SubscriptionId}).",
            notification.ChangeType,
            notification.Resource,
            notification.SubscriptionId);

        await StoreNotificationAsync(notification, message.Category, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a lifecycle notification from the queue, stores it in Table Storage,
    /// and delegates handling to <see cref="ILifecycleNotificationService"/>.
    /// </summary>
    [Function("ProcessLifecycleNotification")]
    public async Task ProcessLifecycleNotificationAsync(
        [QueueTrigger("graph-lifecycle-notifications")] QueueMessage queueMessage,
        CancellationToken cancellationToken)
    {
        var messageText = queueMessage.MessageText.ToString();
        var message = DeserializeMessage(messageText);
        if (message is null)
        {
            _logger.LogError("Discarding unparseable lifecycle notification queue message: {MessageText}", Truncate(messageText, 256));
            return;
        }

        var notification = message.Notification;
        _logger.LogInformation(
            "Processing queued lifecycle event {LifecycleEvent} for subscription {SubscriptionId} expiring {Expiration}.",
            notification.LifecycleEvent,
            notification.SubscriptionId,
            notification.SubscriptionExpirationDateTime);

        await StoreNotificationAsync(notification, message.Category, cancellationToken).ConfigureAwait(false);
        await _lifecycleNotificationService.ProcessAsync(notification, cancellationToken).ConfigureAwait(false);
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

        await _tableClient.UpsertEntityAsync(entity, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private NotificationQueueMessage? DeserializeMessage(string messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationQueueMessage>(messageText, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize notification queue message.");
            return null;
        }
    }

    private static string? GetResourceDataJson(System.Text.Json.JsonElement resourceData)
    {
        return resourceData.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
               resourceData.ValueKind == System.Text.Json.JsonValueKind.Null
            ? null
            : resourceData.GetRawText();
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, maxLength), "…");
}
