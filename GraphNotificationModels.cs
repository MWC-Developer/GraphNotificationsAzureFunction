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
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace GraphNotificationsAzureFunction;

internal sealed class GraphNotificationEnvelope
{
    [JsonPropertyName("value")]
    public GraphNotification[]? Value { get; set; }
}

public sealed class GraphNotification
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("subscriptionExpirationDateTime")]
    public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("lifecycleEvent")]
    public string? LifecycleEvent { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("resourceData")]
    public JsonElement ResourceData { get; set; }
}

/// <summary>Queue message envelope written by the HTTP webhook and read by the queue processor.</summary>
public sealed class NotificationQueueMessage
{
    [JsonPropertyName("notification")]
    public GraphNotification Notification { get; init; } = default!;

    [JsonPropertyName("category")]
    public string Category { get; init; } = default!;
}

/// <summary>Multi-output return type for the change-notification HTTP trigger.</summary>
public sealed class ChangeNotificationWebhookOutput
{
    [HttpResult]
    public IActionResult HttpResponse { get; init; } = default!;

    [QueueOutput("graph-change-notifications")]
    public IEnumerable<string>? QueueMessages { get; init; }
}

/// <summary>Multi-output return type for the lifecycle-notification HTTP trigger.</summary>
public sealed class LifecycleNotificationWebhookOutput
{
    [HttpResult]
    public IActionResult HttpResponse { get; init; } = default!;

    [QueueOutput("graph-lifecycle-notifications")]
    public IEnumerable<string>? QueueMessages { get; init; }
}

public sealed class NotificationEntity : ITableEntity
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
