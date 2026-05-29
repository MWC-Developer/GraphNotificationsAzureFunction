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

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

/// <summary>
/// HTTP-facing Azure Functions that receive Graph change and lifecycle notifications.
/// Each function validates the incoming request and immediately enqueues the raw notifications
/// for asynchronous processing by <see cref="NotificationsQueueProcessor"/>, then returns 202
/// Accepted to Graph within the required timeout window.
/// </summary>
public sealed class NotificationsWebhook
{
    private readonly ILogger<NotificationsWebhook> _logger;
    private readonly IGraphSubscriptionManager _subscriptionManager;
    private readonly GraphSubscriptionSettings _subscriptionSettings;
    private readonly TableClient _tableClient;
    private readonly SubscriptionAdministrationWeb _subscriptionAdministrationWeb;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string? ExpectedClientState =
        Environment.GetEnvironmentVariable("GraphClientState");

    public NotificationsWebhook(
        ILogger<NotificationsWebhook> logger,
        IGraphSubscriptionManager subscriptionManager,
        GraphSubscriptionSettings subscriptionSettings,
        TableClient tableClient)
    {
        _logger = logger;
        _subscriptionManager = subscriptionManager;
        _subscriptionSettings = subscriptionSettings;
        _tableClient = tableClient;
        _subscriptionAdministrationWeb = new SubscriptionAdministrationWeb(
            _logger, _subscriptionManager, _subscriptionSettings, _tableClient);
    }

    /// <summary>
    /// Receives Graph change notifications, validates client state, enqueues each notification,
    /// and returns 202 Accepted immediately — no storage or Graph API calls on this hot path.
    /// </summary>
    [Function("GraphNotifications")]
    public async Task<ChangeNotificationWebhookOutput> HandleNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/notifications")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        if (TryHandleValidation(req, out var validationResponse))
        {
            return new ChangeNotificationWebhookOutput { HttpResponse = validationResponse };
        }

        var payload = await DeserializePayloadAsync(req);
        if (payload?.Value == null || payload.Value.Length == 0)
        {
            _logger.LogWarning("Change notification payload missing or empty.");
            return new ChangeNotificationWebhookOutput { HttpResponse = new BadRequestResult() };
        }

        var changeMessages = new List<string>(payload.Value.Length);
        var lifecycleMessages = new List<string>();
        foreach (var notification in payload.Value)
        {
            if (!IsClientStateValid(notification.ClientState))
            {
                _logger.LogWarning("Client state mismatch for subscription {SubscriptionId}; skipping.", notification.SubscriptionId);
                continue;
            }

            // A lifecycle event (reauthorizationRequired, subscriptionRemoved, missed) landing here
            // means the subscription was registered with LifecycleNotificationUrl pointing at this
            // endpoint instead of /api/graph/lifecycle. Re-route it to the correct queue so renewal
            // still happens, and log a warning so the misconfiguration is visible.
            if (!string.IsNullOrWhiteSpace(notification.LifecycleEvent))
            {
                _logger.LogWarning(
                    "Lifecycle event {LifecycleEvent} for subscription {SubscriptionId} arrived on the change-notification endpoint. " +
                    "Check that Graph:LifecycleNotificationUrl points to /api/graph/lifecycle, not /api/graph/notifications. " +
                    "Re-routing to lifecycle queue.",
                    notification.LifecycleEvent, notification.SubscriptionId);
                lifecycleMessages.Add(SerializeQueueMessage(notification, "lifecycle"));
                continue;
            }

            _logger.LogInformation(
                "Enqueuing change notification {ChangeType} for resource {Resource} (subscription {SubscriptionId}).",
                notification.ChangeType, notification.Resource, notification.SubscriptionId);

            changeMessages.Add(SerializeQueueMessage(notification, "change"));
        }

        return new ChangeNotificationWebhookOutput
        {
            HttpResponse = new AcceptedResult(),
            QueueMessages = changeMessages.Count > 0 ? changeMessages : null,
            LifecycleQueueMessages = lifecycleMessages.Count > 0 ? lifecycleMessages : null
        };
    }

    /// <summary>
    /// Handles the subscription management UI (GET) and management actions (POST).
    /// </summary>
    [Function("GraphNotificationsManagement")]
    public async Task<IActionResult> HandleManagementPage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/manage")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        return await _subscriptionAdministrationWeb
            .HandleManagementRequestAsync(req, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Receives Graph lifecycle notifications (reauthorize, removed, missed) and enqueues each
    /// one for asynchronous handling by <see cref="NotificationsQueueProcessor"/>.
    /// </summary>
    [Function("GraphLifecycleNotifications")]
    public async Task<LifecycleNotificationWebhookOutput> HandleLifecycleNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "graph/lifecycle")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        if (TryHandleValidation(req, out var validationResponse))
        {
            return new LifecycleNotificationWebhookOutput { HttpResponse = validationResponse };
        }

        var payload = await DeserializePayloadAsync(req);
        if (payload?.Value == null || payload.Value.Length == 0)
        {
            _logger.LogWarning("Lifecycle notification payload missing or empty.");
            return new LifecycleNotificationWebhookOutput { HttpResponse = new BadRequestResult() };
        }

        var messages = new List<string>(payload.Value.Length);
        foreach (var notification in payload.Value)
        {
            _logger.LogInformation(
                "Enqueuing lifecycle event {LifecycleEvent} for subscription {SubscriptionId} expiring {Expiration}.",
                notification.LifecycleEvent, notification.SubscriptionId, notification.SubscriptionExpirationDateTime);

            messages.Add(SerializeQueueMessage(notification, "lifecycle"));
        }

        return new LifecycleNotificationWebhookOutput
        {
            HttpResponse = new AcceptedResult(),
            QueueMessages = messages.Count > 0 ? messages : null
        };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

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

        return JsonSerializer.Deserialize<GraphNotificationEnvelope>(body, SerializerOptions);
    }

    private static bool IsClientStateValid(string? clientState) =>
        string.IsNullOrEmpty(ExpectedClientState) ||
        string.Equals(ExpectedClientState, clientState, StringComparison.Ordinal);

    private static string SerializeQueueMessage(GraphNotification notification, string category) =>
        JsonSerializer.Serialize(
            new NotificationQueueMessage { Notification = notification, Category = category },
            SerializerOptions);
}
