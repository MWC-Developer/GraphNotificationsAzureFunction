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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

public interface IGraphSubscriptionManager
{
    Task ReauthorizeAsync(string subscriptionId, CancellationToken cancellationToken);
    Task<string?> CreateSubscriptionAsync(CancellationToken cancellationToken);
    Task<string?> CreateSubscriptionAsync(string resource, string? changeType, CancellationToken cancellationToken);
    Task PerformFullSyncAsync(CancellationToken cancellationToken);
    Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken cancellationToken);
}

public sealed class GraphSubscriptionManager : IGraphSubscriptionManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // HTTP status codes that indicate Graph is throttling the caller.
    private static readonly System.Net.HttpStatusCode[] ThrottleStatusCodes =
    [
        System.Net.HttpStatusCode.TooManyRequests,    // 429
        System.Net.HttpStatusCode.ServiceUnavailable  // 503
    ];

    private readonly GraphSubscriptionSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphSubscriptionManager> _logger;
    private readonly TokenCredential? _credential;
    private readonly GraphRateLimiter _rateLimiter;

    public GraphSubscriptionManager(GraphSubscriptionSettings settings, IHttpClientFactory httpClientFactory, ILogger<GraphSubscriptionManager> logger, GraphRateLimiter rateLimiter)
    {
        _settings = settings;
        _httpClient = httpClientFactory.CreateClient(nameof(GraphSubscriptionManager));
        _logger = logger;
        _rateLimiter = rateLimiter;

        if (_settings.IsConfigured)
        {
            _credential = new ClientSecretCredential(_settings.TenantId!, _settings.ClientId!, _settings.ClientSecret!);
        }
    }

    public async Task ReauthorizeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            _logger.LogWarning("Cannot reauthorize subscription because the subscriptionId is missing.");
            return;
        }

        var relativeUrl = $"subscriptions/{subscriptionId}/reauthorize";
        var response = await SendGraphRequestAsync(HttpMethod.Post, relativeUrl, null, cancellationToken).ConfigureAwait(false);
        await HandleResponseAsync(response, () =>
        {
            _logger.LogInformation("Reauthorized subscription {SubscriptionId}.", subscriptionId);
            return Task.CompletedTask;
        }, subscriptionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> CreateSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.Resource))
        {
            _logger.LogWarning("Graph resource is not configured; cannot create subscription.");
            return null;
        }

        return await CreateSubscriptionInternalAsync(_settings.Resource!, _settings.ChangeType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> CreateSubscriptionAsync(string resource, string? changeType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            _logger.LogWarning("Cannot create subscription because the resource value is missing.");
            return null;
        }

        return await CreateSubscriptionInternalAsync(resource, changeType ?? _settings.ChangeType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> CreateSubscriptionInternalAsync(string resource, string? changeType, CancellationToken cancellationToken)
    {
        if (!EnsureConfigured())
        {
            return null;
        }

        var expiration = DateTimeOffset.UtcNow.Add(_settings.SubscriptionLifetime);
        var effectiveChangeType = NormalizeChangeTypes(changeType ?? _settings.ChangeType);
        if (string.IsNullOrWhiteSpace(effectiveChangeType))
        {
            effectiveChangeType = "updated";
        }
        var payload = new SubscriptionRequest
        {
            ChangeType = effectiveChangeType,
            NotificationUrl = _settings.NotificationUrl!,
            LifecycleNotificationUrl = _settings.LifecycleNotificationUrl!,
            Resource = resource,
            ExpirationDateTime = expiration,
            ClientState = _settings.ClientState
        };

        _logger.LogInformation("Creating subscription for resource {Resource} with change types {ChangeTypes} (expires {Expiration}).", resource, payload.ChangeType, expiration);

        var response = await SendGraphRequestAsync(HttpMethod.Post, "subscriptions", payload, cancellationToken).ConfigureAwait(false);
        string? subscriptionId = null;
        await HandleResponseAsync(response, async () =>
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("id", out var idProperty))
            {
                subscriptionId = idProperty.GetString();
            }

            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                throw new InvalidOperationException($"Graph create subscription response missing id. Payload: {Truncate(json, 512)}");
            }

            _logger.LogInformation("Created subscription {SubscriptionId} expiring at {Expiration}.", subscriptionId, expiration);
        }, subscriptionId, cancellationToken).ConfigureAwait(false);

        return subscriptionId;
    }

    public async Task PerformFullSyncAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        var resourcePath = _settings.Resource ?? string.Empty;
        var relativeUrl = $"{resourcePath.TrimStart('/')}/delta";
        var response = await SendGraphRequestAsync(HttpMethod.Get, relativeUrl, null, cancellationToken).ConfigureAwait(false);
        await HandleResponseAsync(response, async () =>
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Triggered full data sync for resource {Resource}. Payload length: {Length} characters.", resourcePath, payload.Length);
        }, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        if (!EnsureConfigured())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            _logger.LogWarning("Cannot delete subscription because the subscriptionId is missing.");
            return;
        }

        var response = await SendGraphRequestAsync(HttpMethod.Delete, $"subscriptions/{subscriptionId}", null, cancellationToken).ConfigureAwait(false);
        await HandleResponseAsync(response, () =>
        {
            _logger.LogInformation("Deleted subscription {SubscriptionId}.", subscriptionId);
            return Task.CompletedTask;
        }, subscriptionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GraphSubscriptionInfo>> ListSubscriptionsAsync(CancellationToken cancellationToken)
    {
        if (!EnsureConfigured())
        {
            return Array.Empty<GraphSubscriptionInfo>();
        }

        var response = await SendGraphRequestAsync(HttpMethod.Get, "subscriptions", null, cancellationToken).ConfigureAwait(false);
        var subscriptions = Array.Empty<GraphSubscriptionInfo>();
        await HandleResponseAsync(response, async () =>
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.Array)
            {
                subscriptions = valueElement
                    .EnumerateArray()
                    .Select(ParseSubscription)
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray();
            }
        }, null, cancellationToken).ConfigureAwait(false);

        return subscriptions;
    }

    private static GraphSubscriptionInfo? ParseSubscription(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new GraphSubscriptionInfo
        {
            Id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() : null,
            Resource = element.TryGetProperty("resource", out var resourceProp) ? resourceProp.GetString() : null,
            ChangeType = element.TryGetProperty("changeType", out var changeTypeProp) ? changeTypeProp.GetString() : null,
            ExpirationDateTime = element.TryGetProperty("expirationDateTime", out var expirationProp) && expirationProp.ValueKind is JsonValueKind.String
                ? expirationProp.GetDateTimeOffset()
                : (DateTimeOffset?)null,
            ClientState = element.TryGetProperty("clientState", out var clientStateProp) ? clientStateProp.GetString() : null
        };
    }

    private async Task<HttpResponseMessage> SendGraphRequestAsync(HttpMethod method, string relativeUrl, object? payload, CancellationToken cancellationToken)
    {
        if (_credential is null)
        {
            throw new InvalidOperationException("Graph credential is not initialized.");
        }

        var maxAttempts = _settings.MaxRetries + 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Acquire a rate-limit permit (and honour any active throttle embargo) before sending.
            await _rateLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);

            var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _settings.Scope }), cancellationToken).ConfigureAwait(false);
            var request = new HttpRequestMessage(method, BuildRequestUri(relativeUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            if (payload != null)
            {
                request.Content = JsonContent.Create(payload, options: SerializerOptions);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // If Graph is throttling us, signal the limiter and retry (unless we've exhausted attempts).
            if (Array.IndexOf(ThrottleStatusCodes, response.StatusCode) >= 0)
            {
                _rateLimiter.NotifyThrottled(response);

                if (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Graph returned HTTP {Status} on attempt {Attempt}/{Max} for {Method} {Url}. Will retry after throttle delay.",
                        (int)response.StatusCode, attempt, maxAttempts, method, relativeUrl);
                    response.Dispose();
                    continue;
                }

                _logger.LogError(
                    "Graph returned HTTP {Status} on final attempt {Attempt}/{Max} for {Method} {Url}. Giving up.",
                    (int)response.StatusCode, attempt, maxAttempts, method, relativeUrl);
            }

            return response;
        }

        // Unreachable — loop always returns or throws before exhausting.
        throw new InvalidOperationException("Unexpected exit from retry loop.");
    }

    private string BuildRequestUri(string relativePath)
    {
        var baseUrl = _settings.GraphBaseUrl.TrimEnd('/') + "/";
        var trimmedRelative = relativePath.TrimStart('/');
        return baseUrl + trimmedRelative;
    }

    private bool EnsureConfigured()
    {
        var missing = _settings.GetMissingConfigurationFields();
        if (missing.Count == 0 && _credential != null)
        {
            return true;
        }

        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Graph subscription settings are incomplete. Missing values: {MissingFields}.",
                string.Join(", ", missing));
        }
        else if (_credential is null)
        {
            _logger.LogWarning("Graph subscription credentials are unavailable. Verify tenant id, client id, and client secret.");
        }

        return false;
    }

    private static async Task HandleResponseAsync(HttpResponseMessage response, Func<Task> onSuccess, string? subscriptionId, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            await onSuccess().ConfigureAwait(false);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(subscriptionId)
            ? $"Graph call failed with status {(int)response.StatusCode}: {body}"
            : $"Graph call for subscription {subscriptionId} failed with status {(int)response.StatusCode}: {body}";
        throw new InvalidOperationException(message);
    }

    private static string? NormalizeChangeTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Select(item => item.ToLowerInvariant())
            .Distinct()
            .ToArray();

        return normalized.Length == 0
            ? null
            : string.Join(',', normalized);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "…";
    }

    private sealed class SubscriptionRequest
    {
        [JsonPropertyName("changeType")]
        public string ChangeType { get; init; } = default!;

        [JsonPropertyName("notificationUrl")]
        public string NotificationUrl { get; init; } = default!;

        [JsonPropertyName("lifecycleNotificationUrl")]
        public string LifecycleNotificationUrl { get; init; } = default!;

        [JsonPropertyName("resource")]
        public string Resource { get; init; } = default!;

        [JsonPropertyName("expirationDateTime")]
        public DateTimeOffset ExpirationDateTime { get; init; }

        [JsonPropertyName("clientState")]
        public string? ClientState { get; init; }
    }
}

public sealed record GraphSubscriptionInfo
{
    public string? Id { get; init; }
    public string? Resource { get; init; }
    public DateTimeOffset? ExpirationDateTime { get; init; }
    public string? ChangeType { get; init; }
    public string? ClientState { get; init; }
}
