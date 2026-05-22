using System;
using System.Text.Json;
using System.Text.Json.Serialization;

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
