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

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

public sealed class SubscriptionAdministrationWeb
{
    private readonly ILogger _logger;
    private readonly IGraphSubscriptionManager _subscriptionManager;
    private readonly GraphSubscriptionSettings _subscriptionSettings;
    private readonly TableClient _tableClient;
    private const int MaxNotificationItems = 100;
    private const string UserIdPlaceholder = "{id}";
    private const string FeedbackResponseQueryName = "response";
    private const string FeedbackResponseValue = "feedback";

    public SubscriptionAdministrationWeb(
        ILogger logger,
        IGraphSubscriptionManager subscriptionManager,
        GraphSubscriptionSettings subscriptionSettings,
        TableClient tableClient)
    {
        _logger = logger;
        _subscriptionManager = subscriptionManager;
        _subscriptionSettings = subscriptionSettings;
        _tableClient = tableClient;
    }

    public async Task<IActionResult> HandleManagementRequestAsync(HttpRequest req, CancellationToken cancellationToken)
    {
        if (IsFeedbackDataRequest(req))
        {
            return BuildFeedbackDataResponse(req);
        }

        if (HttpMethods.Post.Equals(req.Method, StringComparison.OrdinalIgnoreCase))
        {
            var postStatusMessages = new List<string>();
            var postErrorMessages = new List<string>();
            await ProcessManagementActionAsync(req, postStatusMessages, postErrorMessages, cancellationToken).ConfigureAwait(false);

            return BuildPostRedirectResult(req, postStatusMessages, postErrorMessages);
        }

        var inlineStatusMessages = new List<string>();
        var inlineErrorMessages = new List<string>();

        IReadOnlyList<GraphSubscriptionInfo> subscriptions = Array.Empty<GraphSubscriptionInfo>();
        try
        {
            subscriptions = await _subscriptionManager.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list subscriptions.");
            inlineErrorMessages.Add($"Failed to load subscriptions: {ex.Message}");
        }

        IReadOnlyList<NotificationEntity> notifications = Array.Empty<NotificationEntity>();
        try
        {
            notifications = await GetRecentNotificationsAsync(MaxNotificationItems, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load stored notifications.");
            inlineErrorMessages.Add($"Failed to load received events: {ex.Message}");
        }

        var inlineFeedbackToken = SerializeFeedbackPayload(inlineStatusMessages, inlineErrorMessages);
        var managementPageUrl = BuildManagementPageUrl(req);
        var html = BuildManagementPageHtml(subscriptions, notifications, inlineFeedbackToken, managementPageUrl);
        return new ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = StatusCodes.Status200OK
        };
    }

    private IActionResult BuildPostRedirectResult(
        HttpRequest request,
        IReadOnlyCollection<string> statusMessages,
        IReadOnlyCollection<string> errorMessages)
    {
        var redirectUrl = BuildRedirectUrl(request, statusMessages, errorMessages);
        return new RedirectResult(redirectUrl, permanent: false, preserveMethod: false);
    }

    private static string BuildRedirectUrl(
        HttpRequest request,
        IReadOnlyCollection<string> statusMessages,
        IReadOnlyCollection<string> errorMessages)
    {
        var path = ResolveManagePath(request);
        var queryParts = new List<string>();

        var feedbackToken = SerializeFeedbackPayload(statusMessages, errorMessages);
        if (!string.IsNullOrEmpty(feedbackToken))
        {
            queryParts.Add($"feedback={WebUtility.UrlEncode(feedbackToken)}");
        }

        return queryParts.Count == 0
            ? path
            : string.Concat(path, "?", string.Join("&", queryParts));
    }

    private static bool IsFeedbackDataRequest(HttpRequest request) =>
        string.Equals(
            request.Query[FeedbackResponseQueryName],
            FeedbackResponseValue,
            StringComparison.OrdinalIgnoreCase);

    private IActionResult BuildFeedbackDataResponse(HttpRequest request)
    {
        var payload = DeserializeFeedbackPayload(request.Query["feedback"].FirstOrDefault());
        var response = new
        {
            statusMessages = payload?.StatusMessages ?? Array.Empty<string>(),
            errorMessages = payload?.ErrorMessages ?? Array.Empty<string>()
        };

        return new JsonResult(response)
        {
            ContentType = "application/json",
            StatusCode = StatusCodes.Status200OK
        };
    }

    private static string ResolveManagePath(HttpRequest request)
    {
        var combined = request.PathBase.Add(request.Path);
        return combined.HasValue ? combined.Value! : "/api/graph/manage";
    }

    private static string BuildManagementPageUrl(HttpRequest request)
    {
        var path = ResolveManagePath(request);
        var scheme = string.IsNullOrWhiteSpace(request.Scheme) ? "https" : request.Scheme;
        var host = request.Host.HasValue ? request.Host.Value : string.Empty;
        return string.IsNullOrWhiteSpace(host)
            ? path
            : string.Concat(scheme, "://", host, path);
    }

    private static string? SerializeFeedbackPayload(
        IReadOnlyCollection<string> statusMessages,
        IReadOnlyCollection<string> errorMessages)
    {
        var hasStatus = statusMessages is { Count: > 0 };
        var hasErrors = errorMessages is { Count: > 0 };

        if (!hasStatus && !hasErrors)
        {
            return null;
        }

        var payload = new FeedbackPayload
        {
            StatusMessages = statusMessages.ToArray(),
            ErrorMessages = errorMessages.ToArray()
        };

        var json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static FeedbackPayload? DeserializeFeedbackPayload(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<FeedbackPayload>(json);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task ProcessManagementActionAsync(HttpRequest request, ICollection<string> statusMessages, ICollection<string> errorMessages, CancellationToken cancellationToken)
    {
        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var action = form["action"].FirstOrDefault();
        switch (action)
        {
            case "create":
                var userId = form["userId"].FirstOrDefault()?.Trim();
                var selectedResources = form["resourceKeys"]
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (selectedResources.Count == 0)
                {
                    errorMessages.Add("Select at least one resource option before creating a subscription.");
                    return;
                }

                var missingSettings = _subscriptionSettings.GetMissingConfigurationFields();
                if (missingSettings.Count > 0)
                {
                    var details = string.Join(", ", missingSettings);
                    var message = $"Graph subscription settings are incomplete. Configure these values before creating subscriptions: {details}.";
                    _logger.LogWarning(message);
                    errorMessages.Add(message);
                    return;
                }

                foreach (var resourceKey in selectedResources)
                {
                    var option = _subscriptionSettings.ResourceOptions.FirstOrDefault(o => string.Equals(o.Key, resourceKey, StringComparison.OrdinalIgnoreCase));
                    if (option is null)
                    {
                        errorMessages.Add($"Unknown resource option '{resourceKey}'.");
                        continue;
                    }

                    if (!option.IsConfigured)
                    {
                        errorMessages.Add($"Resource '{option.DisplayName}' is not configured. Update Graph settings to use this option.");
                        continue;
                    }

                    if (ResourceRequiresUserId(option.Resource!) && string.IsNullOrWhiteSpace(userId))
                    {
                        errorMessages.Add($"Enter a user id to create {option.DisplayName} subscription.");
                        continue;
                    }

                    try
                    {
                        var resolvedResource = ApplyUserIdToResource(option.Resource!, userId);
                        var createdSubscriptionId = await _subscriptionManager.CreateSubscriptionAsync(resolvedResource, option.ChangeType, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(createdSubscriptionId))
                        {
                            errorMessages.Add($"Graph did not return an identifier for {option.DisplayName}.");
                        }
                        else
                        {
                            statusMessages.Add($"Created {option.DisplayName} subscription ({createdSubscriptionId}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to create {DisplayName} subscription.", option.DisplayName);
                        errorMessages.Add($"Failed to create {option.DisplayName} subscription: {ex.Message}");
                    }
                }
                break;
            case "stop":
                var subscriptionId = form["subscriptionId"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(subscriptionId))
                {
                    subscriptionId = form["subscriptionSelect"].FirstOrDefault();
                }

                if (string.IsNullOrWhiteSpace(subscriptionId))
                {
                    errorMessages.Add("Enter or select a subscription id to stop.");
                    return;
                }

                try
                {
                    await _subscriptionManager.DeleteSubscriptionAsync(subscriptionId!, cancellationToken).ConfigureAwait(false);
                    statusMessages.Add($"Stopped subscription {subscriptionId}.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to stop subscription {SubscriptionId}.", subscriptionId);
                    errorMessages.Add($"Failed to stop subscription {subscriptionId}: {ex.Message}");
                }
                break;
            case "stop-all":
                IReadOnlyList<GraphSubscriptionInfo> activeSubscriptions;
                try
                {
                    activeSubscriptions = await _subscriptionManager.ListSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to list subscriptions before stopping all.");
                    errorMessages.Add($"Failed to retrieve subscriptions: {ex.Message}");
                    break;
                }

                var subscriptionsToStop = activeSubscriptions
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id))
                    .Select(s => s.Id!)
                    .ToList();

                if (subscriptionsToStop.Count == 0)
                {
                    statusMessages.Add("No active subscriptions to stop.");
                    break;
                }

                var stoppedCount = 0;
                foreach (var activeId in subscriptionsToStop)
                {
                    try
                    {
                        await _subscriptionManager.DeleteSubscriptionAsync(activeId, cancellationToken).ConfigureAwait(false);
                        stoppedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to stop subscription {SubscriptionId} while stopping all.", activeId);
                        errorMessages.Add($"Failed to stop subscription {activeId}: {ex.Message}");
                    }
                }

                if (stoppedCount > 0)
                {
                    statusMessages.Add($"Stopped {stoppedCount} subscription(s).");
                }

                break;
            case "clear-events":
                try
                {
                    var cleared = await ClearEventsAsync(cancellationToken).ConfigureAwait(false);
                    statusMessages.Add(cleared > 0
                        ? $"Cleared {cleared} stored event(s)."
                        : "No events to clear.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to clear stored events.");
                    errorMessages.Add($"Failed to clear events: {ex.Message}");
                }

                break;
            case "list":
                statusMessages.Add("Subscription list refreshed.");
                break;
            default:
                errorMessages.Add("Unknown management action.");
                break;
        }
    }

    private async Task<IReadOnlyList<NotificationEntity>> GetRecentNotificationsAsync(int maxItems, CancellationToken cancellationToken)
    {
        var items = new List<NotificationEntity>(maxItems);
        await foreach (var entity in _tableClient.QueryAsync<NotificationEntity>(maxPerPage: maxItems, cancellationToken: cancellationToken))
        {
            items.Add(entity);
            if (items.Count >= maxItems)
            {
                break;
            }
        }

        return items
            .OrderByDescending(entity => entity.ReceivedUtc)
            .Take(maxItems)
            .ToList();
    }

    private async Task<int> ClearEventsAsync(CancellationToken cancellationToken)
    {
        var deleted = 0;
        await foreach (var entity in _tableClient.QueryAsync<NotificationEntity>(cancellationToken: cancellationToken))
        {
            await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All, cancellationToken);
            deleted++;
        }

        return deleted;
    }

    private string BuildManagementPageHtml(
        IReadOnlyList<GraphSubscriptionInfo> subscriptions,
        IReadOnlyList<NotificationEntity> notifications,
        string? inlineFeedbackToken,
        string managementPageUrl)
    {
        var builder = new StringBuilder();
        var inlineFeedbackValue = string.IsNullOrEmpty(inlineFeedbackToken)
            ? string.Empty
            : HtmlEncode(inlineFeedbackToken);
        var configuredTenantId = HtmlEncode(_subscriptionSettings.TenantId ?? string.Empty);
        var configuredClientId = HtmlEncode(_subscriptionSettings.ClientId ?? string.Empty);
        var configuredScope = HtmlEncode(_subscriptionSettings.Scope ?? string.Empty);
        var delegatedRedirectUri = HtmlEncode(managementPageUrl);
        var hasConfiguredSecret = !string.IsNullOrWhiteSpace(_subscriptionSettings.ClientSecret);
        var clientSecretHelperText = hasConfiguredSecret
            ? "A client secret is already configured in application settings; supply a new value only when rotating."
            : "After creating a secret, add it to Graph:ClientSecret (not stored in this page).";
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("    <meta charset=\"utf-8\" />");
        builder.AppendLine("    <title>Graph Subscription Management</title>");
        builder.AppendLine("    <style>");
        builder.AppendLine("        body { font-family: Segoe UI, Arial, sans-serif; margin: 0; padding: 20px; background: #f2f2f2; }");
        builder.AppendLine("        h1 { margin-top: 0; }");
        builder.AppendLine("        .panel { background: #fff; padding: 16px; border-radius: 6px; margin-bottom: 16px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }");
        builder.AppendLine("        .auth-configuration { border-left: 4px solid #0078d4; }");
        builder.AppendLine("        .auth-flow-selector { display: flex; gap: 16px; flex-wrap: wrap; margin-bottom: 12px; }");
        builder.AppendLine("        .auth-flow-selector label { display: flex; gap: 6px; align-items: center; font-weight: 600; }");
        builder.AppendLine("        .auth-flow-section { display: none; border-top: 1px solid #eee; padding-top: 12px; margin-top: 12px; }");
        builder.AppendLine("        .auth-flow-section.active { display: block; }");
        builder.AppendLine("        .auth-config-grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); }");
        builder.AppendLine("        .copyable-input { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }");
        builder.AppendLine("        .copyable-input input { flex: 1; min-width: 220px; }");
        builder.AppendLine("        .copy-button { padding: 6px 12px; border: 1px solid #0078d4; background: #0078d4; color: #fff; border-radius: 4px; cursor: pointer; }");
        builder.AppendLine("        .copy-button:focus { outline: 2px solid #004578; outline-offset: 2px; }");
        builder.AppendLine("        .copy-button:disabled { opacity: 0.65; cursor: default; }");
        builder.AppendLine("        .actions { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); align-items: start; }");
        builder.AppendLine("        @media (min-width: 1100px) {");
        builder.AppendLine("            .actions { grid-template-columns: minmax(320px, 1fr) minmax(220px, 0.5fr) minmax(420px, 1.5fr); }");
        builder.AppendLine("        }");
        builder.AppendLine("        .checkbox-list label { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }");
        builder.AppendLine("        .checkbox-list span { font-size: 0.85rem; color: #555; }");
        builder.AppendLine("        .checkbox-list span.helper-text { font-size: 0.75rem; color: #b34700; }");
        builder.AppendLine("        .checkbox-list { max-height: 40vh; overflow-y: auto; padding-right: 4px; }");
        builder.AppendLine("        .resource-group-header { font-size: 0.75rem; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; color: #0078d4; border-bottom: 1px solid #dde; margin: 8px 0 6px; padding-bottom: 2px; }");
        builder.AppendLine("        .form-field { display: flex; flex-direction: column; gap: 4px; margin-bottom: 10px; }");
        builder.AppendLine("        .helper-text { font-size: 0.75rem; color: #b34700; }");
        builder.AppendLine("        .resource-path { font-family: Consolas, monospace; font-size: 0.8rem; color: #333; display: block; }");
        builder.AppendLine("        table { width: 100%; border-collapse: collapse; }");
        builder.AppendLine("        th, td { padding: 8px; border-bottom: 1px solid #ddd; text-align: left; font-size: 0.9rem; }");
        builder.AppendLine("        th { background: #fafafa; }");
        builder.AppendLine("        .table-wrapper.subscription-scroll { max-height: 25vh; overflow-y: auto; overflow-x: auto; }");
        builder.AppendLine("        .events { max-height: 50vh; overflow-y: auto; }");
        builder.AppendLine("        .event-json { font-family: Consolas, monospace; white-space: pre-wrap; font-size: 0.8rem; color: #444; }");
        builder.AppendLine("        textarea { width: 100%; }");
        builder.AppendLine("        input[readonly] { background: #f5f5f5; }");
        builder.AppendLine("        .refresh-controls { display: flex; gap: 12px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }");
        builder.AppendLine("        .refresh-controls label { font-weight: 600; }");
        builder.AppendLine("        .refresh-controls select { min-width: 120px; padding: 4px; }");
        builder.AppendLine("        .clear-events-form { margin-left: auto; }");
        builder.AppendLine("        .feedback-container { display: flex; flex-direction: column; gap: 8px; margin: 10px 0; }");
        builder.AppendLine("        .feedback { position: relative; padding: 10px 36px 10px 12px; border-radius: 4px; font-size: 0.9rem; }");
        builder.AppendLine("        .feedback.success { background: #e6f4ea; color: #0f5132; }");
        builder.AppendLine("        .feedback.error { background: #f8d7da; color: #842029; }");
        builder.AppendLine("        .feedback-close { position: absolute; top: 6px; right: 8px; border: none; background: transparent; color: inherit; font-size: 1.1rem; cursor: pointer; }");
        builder.AppendLine("        .feedback-close:focus { outline: 2px solid currentColor; outline-offset: 2px; }");
        builder.AppendLine("    </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("    <h1>Graph Subscription Management</h1>");
        builder.AppendLine($"    <div id=\"feedbackContainer\" class=\"feedback-container\" data-inline-feedback=\"{inlineFeedbackValue}\"></div>");
        builder.AppendLine("    <div class=\"panel auth-configuration\">");
        builder.AppendLine("        <h2>Entra ID App Registration</h2>");
        builder.AppendLine("        <p>Capture the identity platform details for the Graph access that backs this environment. These values are stored only in your browser to help document the registration.</p>");
        builder.AppendLine("        <div class=\"auth-flow-selector\">");
        builder.AppendLine("            <label><input type=\"radio\" name=\"authFlowMode\" value=\"application\" checked /> Application (client credentials)</label>");
        builder.AppendLine("            <label><input type=\"radio\" name=\"authFlowMode\" value=\"delegated\" /> Delegated (authorization code)</label>");
        builder.AppendLine("        </div>");
        builder.AppendLine("        <div class=\"auth-flow-section active\" data-flow=\"application\">");
        builder.AppendLine("            <p class=\"helper-text\">Use this flow when the Azure Function acquires tokens with its own identity.</p>");
        builder.AppendLine("            <div class=\"auth-config-grid\">");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"appFlowTenantId\">Tenant (directory) id</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"appFlowTenantId\" name=\"appFlowTenantId\" data-storage-key=\"tenantId\" data-initial-value=\"{configuredTenantId}\" value=\"{configuredTenantId}\" placeholder=\"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx\" />");
        builder.AppendLine("                    <span class=\"helper-text\">Matches Graph:TenantId.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"appFlowClientId\">Client (application) id</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"appFlowClientId\" name=\"appFlowClientId\" data-storage-key=\"clientId\" data-initial-value=\"{configuredClientId}\" value=\"{configuredClientId}\" placeholder=\"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx\" />");
        builder.AppendLine("                    <span class=\"helper-text\">Matches Graph:ClientId.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"appFlowClientSecret\">Client secret</label>");
        builder.AppendLine("                    <input type=\"password\" id=\"appFlowClientSecret\" name=\"appFlowClientSecret\" placeholder=\"Enter the secret (not persisted)\" autocomplete=\"off\" />");
        builder.AppendLine($"                    <span class=\"helper-text\">{HtmlEncode(clientSecretHelperText)}</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"appFlowScope\">Token scope</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"appFlowScope\" name=\"appFlowScope\" data-storage-key=\"scope\" data-initial-value=\"{configuredScope}\" value=\"{configuredScope}\" placeholder=\"https://graph.microsoft.com/.default\" />");
        builder.AppendLine("                    <span class=\"helper-text\">Use the /.default scope for application permissions.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("            </div>");
        builder.AppendLine("        </div>");
        builder.AppendLine("        <div class=\"auth-flow-section\" data-flow=\"delegated\">");
        builder.AppendLine("            <p class=\"helper-text\">Use delegated access when a signed-in user authorizes the app. Tokens will return to this page to complete the flow.</p>");
        builder.AppendLine("            <div class=\"auth-config-grid\">");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"delegatedTenantId\">Tenant (directory) id</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"delegatedTenantId\" name=\"delegatedTenantId\" data-storage-key=\"tenantId\" data-initial-value=\"{configuredTenantId}\" value=\"{configuredTenantId}\" placeholder=\"common or tenant id\" />");
        builder.AppendLine("                    <span class=\"helper-text\">Use 'common' for multi-tenant apps.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"delegatedClientId\">Client (application) id</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"delegatedClientId\" name=\"delegatedClientId\" data-storage-key=\"clientId\" data-initial-value=\"{configuredClientId}\" value=\"{configuredClientId}\" placeholder=\"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx\" />");
        builder.AppendLine("                    <span class=\"helper-text\">Matches the same app registration id.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"delegatedScopes\">Delegated scopes</label>");
        builder.AppendLine($"                    <input type=\"text\" id=\"delegatedScopes\" name=\"delegatedScopes\" data-storage-key=\"delegatedScopes\" data-initial-value=\"{configuredScope}\" value=\"{configuredScope}\" placeholder=\"User.Read Mail.Read\" />");
        builder.AppendLine("                    <span class=\"helper-text\">List space-separated scopes to request from Graph.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"delegatedRedirectUri\">Redirect URI</label>");
        builder.AppendLine("                    <div class=\"copyable-input\">");
        builder.AppendLine($"                        <input type=\"text\" id=\"delegatedRedirectUri\" name=\"delegatedRedirectUri\" value=\"{delegatedRedirectUri}\" readonly />");
        builder.AppendLine("                        <button type=\"button\" class=\"copy-button\" data-copy-target=\"delegatedRedirectUri\">Copy</button>");
        builder.AppendLine("                    </div>");
        builder.AppendLine("                    <span class=\"helper-text\">Add this URL to the Web platform redirect URIs for the registration.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("            </div>");
        builder.AppendLine("        </div>");
        builder.AppendLine("    </div>");

        builder.AppendLine("    <div class=\"actions\">");
        builder.AppendLine("        <div class=\"panel create-subscription\">");
        builder.AppendLine("            <h2>Create Subscription</h2>");
        builder.AppendLine("            <form method=\"post\">");
        builder.AppendLine("                <input type=\"hidden\" name=\"action\" value=\"create\" />");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"userId\">User id</label>");
        builder.AppendLine("                    <input type=\"text\" id=\"userId\" name=\"userId\" placeholder=\"Enter user object id or userPrincipalName\" value=\"\" />");
        builder.AppendLine("                    <span class=\"helper-text\">If a resource path contains {id}, this value replaces it.</span>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"checkbox-list\">");

        var defaultChangeTypes = string.IsNullOrWhiteSpace(_subscriptionSettings.ChangeType)
            ? "-"
            : _subscriptionSettings.ChangeType;

        string? currentCategory = null;
        foreach (var option in _subscriptionSettings.ResourceOptions)
        {
            if (!string.Equals(option.Category, currentCategory, StringComparison.Ordinal))
            {
                if (currentCategory is not null)
                {
                    builder.AppendLine("                </div>");  // close previous group
                }
                currentCategory = option.Category;
                builder.AppendLine($"                <div class=\"resource-group\">");
                builder.AppendLine($"                    <div class=\"resource-group-header\">{HtmlEncode(currentCategory)}</div>");
            }

            var disabled = option.IsConfigured ? string.Empty : " disabled";
            var resourceTemplate = option.Resource ?? string.Empty;
            var encodedTemplate = HtmlEncode(resourceTemplate);
            var templateAttribute = option.IsConfigured ? $" data-resource-template=\"{encodedTemplate}\"" : string.Empty;
            var note = option.IsConfigured ? encodedTemplate : "Not configured";
            var changeTypes = string.IsNullOrWhiteSpace(option.ChangeType) ? defaultChangeTypes : option.ChangeType;
            builder.AppendLine("                    <label>");
            builder.AppendLine($"                        <input type=\"checkbox\" name=\"resourceKeys\" value=\"{HtmlEncode(option.Key)}\"{disabled} />");
            builder.AppendLine($"                        <strong>{HtmlEncode(option.DisplayName)}</strong>");
            builder.AppendLine($"                        <span class=\"resource-path\"{templateAttribute}>{note}</span>");
            builder.AppendLine($"                        <span class=\"helper-text\">({HtmlEncode(changeTypes ?? "missing")})</span>");
            builder.AppendLine("                    </label>");
        }

        if (currentCategory is not null)
        {
            builder.AppendLine("                </div>");  // close last group
        }

        builder.AppendLine("                </div>");
        builder.AppendLine("                <button type=\"submit\">Create Subscription</button>");
        builder.AppendLine("            </form>");
        builder.AppendLine("        </div>");

        builder.AppendLine("        <div class=\"panel stop-subscription\">");
        builder.AppendLine("            <h2>Stop Subscription</h2>");
        builder.AppendLine("            <form method=\"post\">");
        builder.AppendLine("                <input type=\"hidden\" name=\"action\" value=\"stop\" />");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"subscriptionId\">Subscription id</label>");
        builder.AppendLine("                    <input type=\"text\" id=\"subscriptionId\" name=\"subscriptionId\" placeholder=\"Enter subscription id\" />");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <div class=\"form-field\">");
        builder.AppendLine("                    <label for=\"subscriptionSelect\">Or select an active subscription</label>");
        builder.AppendLine("                    <select id=\"subscriptionSelect\" name=\"subscriptionSelect\">");
        builder.AppendLine("                        <option value=\"\">-- Select --</option>");

        foreach (var subscription in subscriptions.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            var label = $"{subscription.Id} ({subscription.Resource})";
            builder.AppendLine($"                        <option value=\"{HtmlEncode(subscription.Id)}\">{HtmlEncode(label)}</option>");
        }

        builder.AppendLine("                    </select>");
        builder.AppendLine("                </div>");
        builder.AppendLine("                <button type=\"submit\">Stop Subscription</button>");
        builder.AppendLine("            </form>");
        builder.AppendLine("            <form method=\"post\" style=\"margin-top: 10px;\">");
        builder.AppendLine("                <input type=\"hidden\" name=\"action\" value=\"stop-all\" />");
        builder.AppendLine("                <button type=\"submit\" onclick=\"return confirm('Stop all active subscriptions?');\">Stop All Subscriptions</button>");
        builder.AppendLine("            </form>");
        builder.AppendLine("        </div>");

        builder.AppendLine("        <div class=\"panel active-subscriptions\">");
        builder.AppendLine("            <h2>Active Subscriptions</h2>");
        builder.AppendLine("            <form method=\"post\" style=\"margin-bottom: 10px;\">");
        builder.AppendLine("                <input type=\"hidden\" name=\"action\" value=\"list\" />");
        builder.AppendLine("                <button type=\"submit\">Refresh Active Subscriptions</button>");
        builder.AppendLine("            </form>");
        builder.AppendLine("            <div class=\"table-wrapper subscription-scroll\">");
        builder.AppendLine("                <table>");
        builder.AppendLine("                    <thead><tr><th>Id</th><th>Resource</th><th>Change Type</th><th>Client State</th><th>Expires (UTC)</th></tr></thead>");
        builder.AppendLine("                    <tbody>");

        if (subscriptions.Count == 0)
        {
            builder.AppendLine("                        <tr><td colspan=\"5\">No active subscriptions found.</td></tr>");
        }
        else
        {
            foreach (var subscription in subscriptions)
            {
                builder.AppendLine("                        <tr>");
                builder.AppendLine($"                            <td>{HtmlEncode(subscription.Id)}</td>");
                builder.AppendLine($"                            <td>{HtmlEncode(subscription.Resource)}</td>");
                builder.AppendLine($"                            <td>{HtmlEncode(subscription.ChangeType)}</td>");
                builder.AppendLine($"                            <td>{HtmlEncode(subscription.ClientState)}</td>");
                builder.AppendLine($"                            <td>{HtmlEncode(FormatDate(subscription.ExpirationDateTime))}</td>");
                builder.AppendLine("                        </tr>");
            }
        }

        builder.AppendLine("                    </tbody>");
        builder.AppendLine("                </table>");
        builder.AppendLine("            </div>");
        builder.AppendLine("        </div>");
        builder.AppendLine("    </div>");

        builder.AppendLine("    <div class=\"panel events\">");
        builder.AppendLine("        <h2>Received Events</h2>");
        builder.AppendLine("        <div class=\"refresh-controls\">");
        builder.AppendLine("            <button type=\"button\" id=\"refreshEventsButton\">Refresh Events</button>");
        builder.AppendLine("            <label for=\"autoRefreshSelect\">Auto refresh</label>");
        builder.AppendLine("            <select id=\"autoRefreshSelect\" aria-label=\"Automatic refresh interval\">");
        builder.AppendLine("                <option value=\"0\">Off</option>");
        builder.AppendLine("                <option value=\"10\">10s</option>");
        builder.AppendLine("                <option value=\"30\">30s</option>");
        builder.AppendLine("                <option value=\"60\">1m</option>");
        builder.AppendLine("                <option value=\"300\">5m</option>");
        builder.AppendLine("                <option value=\"540\">9m</option>");
        builder.AppendLine("                <option value=\"600\">10m</option>");
        builder.AppendLine("                <option value=\"1800\">30m</option>");
        builder.AppendLine("                <option value=\"3600\">60m</option>");
        builder.AppendLine("            </select>");
        builder.AppendLine("            <form method=\"post\" class=\"clear-events-form\" onsubmit=\"return confirm('Clear all stored events?');\">");
        builder.AppendLine("                <input type=\"hidden\" name=\"action\" value=\"clear-events\" />");
        builder.AppendLine("                <button type=\"submit\">Clear Events</button>");
        builder.AppendLine("            </form>");
        builder.AppendLine("        </div>");
        builder.AppendLine("        <table>");
        builder.AppendLine("            <thead><tr><th>Received (UTC)</th><th>Category</th><th>Change Type</th><th>Lifecycle Event</th><th>Resource</th><th>Subscription</th><th>Expires</th></tr></thead>");
        builder.AppendLine("            <tbody>");

        if (notifications.Count == 0)
        {
            builder.AppendLine("                <tr><td colspan=\"7\">No events recorded.</td></tr>");
        }
        else
        {
            foreach (var notification in notifications)
            {
                builder.AppendLine("                <tr>");
                builder.AppendLine($"                    <td>{HtmlEncode(FormatDate(notification.ReceivedUtc))}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(notification.Category)}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(notification.ChangeType)}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(notification.LifecycleEvent)}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(notification.Resource)}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(notification.PartitionKey)}</td>");
                builder.AppendLine($"                    <td>{HtmlEncode(FormatDate(notification.SubscriptionExpirationDateTime))}</td>");
                builder.AppendLine("                </tr>");
            }
        }

        builder.AppendLine("            </tbody>");
        builder.AppendLine("        </table>");
        builder.AppendLine("    </div>");
        builder.AppendLine("    <script>");
        builder.AppendLine("        (function () {");
        builder.AppendLine("            const container = document.getElementById('feedbackContainer');");
        builder.AppendLine("            if (!container) { return; }");
        builder.AppendLine("            const inlineToken = container.getAttribute('data-inline-feedback');");
        builder.AppendLine("            const decodeToken = (encoded) => {");
        builder.AppendLine("                if (!encoded) { return null; }");
        builder.AppendLine("                try {");
        builder.AppendLine("                    const json = window.atob(encoded);");
        builder.AppendLine("                    return JSON.parse(json);");
        builder.AppendLine("                } catch (error) {");
        builder.AppendLine("                    return null;");
        builder.AppendLine("                }");
        builder.AppendLine("            };");
        builder.AppendLine("            const addMessage = (text, kind) => {");
        builder.AppendLine("                if (!text) { return; }");
        builder.AppendLine("                const messageElement = document.createElement('div');");
        builder.AppendLine("                messageElement.className = 'feedback ' + kind;");
        builder.AppendLine("                const textSpan = document.createElement('span');");
        builder.AppendLine("                textSpan.textContent = text;");
        builder.AppendLine("                messageElement.appendChild(textSpan);");
        builder.AppendLine("                const closeButton = document.createElement('button');");
        builder.AppendLine("                closeButton.type = 'button';");
        builder.AppendLine("                closeButton.className = 'feedback-close';");
        builder.AppendLine("                closeButton.setAttribute('aria-label', 'Dismiss message');");
        builder.AppendLine("                closeButton.textContent = '×';");
        builder.AppendLine("                closeButton.addEventListener('click', () => messageElement.remove());");
        builder.AppendLine("                messageElement.appendChild(closeButton);");
        builder.AppendLine("                container.appendChild(messageElement);");
        builder.AppendLine("            };");
        builder.AppendLine("            const renderPayload = (payload) => {");
        builder.AppendLine("                if (!payload) { return; }");
        builder.AppendLine("                const statuses = Array.isArray(payload.statusMessages) ? payload.statusMessages : [];");
        builder.AppendLine("                const errors = Array.isArray(payload.errorMessages) ? payload.errorMessages : [];");
        builder.AppendLine("                statuses.forEach(message => addMessage(message, 'success')); ");
        builder.AppendLine("                errors.forEach(message => addMessage(message, 'error')); ");
        builder.AppendLine("            };");
        builder.AppendLine("            const clearFeedbackQueryParam = () => {");
        builder.AppendLine("                if (!window.history || !window.history.replaceState) { return; }");
        builder.AppendLine("                const currentUrl = new URL(window.location.href);");
        builder.AppendLine("                if (!currentUrl.searchParams.has('feedback')) { return; }");
        builder.AppendLine("                currentUrl.searchParams.delete('feedback');");
        builder.AppendLine("                window.history.replaceState({}, document.title, currentUrl.toString());");
        builder.AppendLine("            };");
        builder.AppendLine("            const requestFeedback = (token) => {");
        builder.AppendLine("                if (!token) { return; }");
        builder.AppendLine("                const requestUrl = new URL(window.location.href);");
        builder.AppendLine("                requestUrl.searchParams.set('response', 'feedback');");
        builder.AppendLine("                requestUrl.searchParams.set('feedback', token);");
        builder.AppendLine("                fetch(requestUrl.toString(), { headers: { 'Accept': 'application/json' }, cache: 'no-store' })");
        builder.AppendLine("                    .then(response => {");
        builder.AppendLine("                        if (!response.ok) { throw new Error('Failed'); }");
        builder.AppendLine("                        return response.json();");
        builder.AppendLine("                    })");
        builder.AppendLine("                    .then(renderPayload)");
        builder.AppendLine("                    .catch(() => addMessage('Failed to load feedback details.', 'error'))");
        builder.AppendLine("                    .finally(() => clearFeedbackQueryParam());");
        builder.AppendLine("            };");
        builder.AppendLine("            renderPayload(decodeToken(inlineToken));");
        builder.AppendLine("            const url = new URL(window.location.href);");
        builder.AppendLine("            const feedbackToken = url.searchParams.get('feedback');");
        builder.AppendLine("            if (feedbackToken) {");
        builder.AppendLine("                requestFeedback(feedbackToken);");
        builder.AppendLine("            } else {");
        builder.AppendLine("                clearFeedbackQueryParam();");
        builder.AppendLine("            }");
        builder.AppendLine("        })();");
        builder.AppendLine("        (function () {");
        builder.AppendLine("            const flowRadios = document.querySelectorAll('input[name=\"authFlowMode\"]');");
        builder.AppendLine("            if (flowRadios.length === 0) { return; }");
        builder.AppendLine("            const sections = document.querySelectorAll('.auth-flow-section');");
        builder.AppendLine("            const hasStorage = () => typeof window !== 'undefined' && window.localStorage;");
        builder.AppendLine("            const storagePrefix = 'graphAuthConfig:';");
        builder.AppendLine("            const setFlow = (value) => {");
        builder.AppendLine("                sections.forEach(section => {");
        builder.AppendLine("                    const isMatch = section.getAttribute('data-flow') === value;");
        builder.AppendLine("                    section.classList.toggle('active', isMatch);");
        builder.AppendLine("                });");
        builder.AppendLine("                if (hasStorage()) {");
        builder.AppendLine("                    window.localStorage.setItem(storagePrefix + 'flow', value);");
        builder.AppendLine("                }");
        builder.AppendLine("            };");
        builder.AppendLine("            const storedFlow = hasStorage() ? window.localStorage.getItem(storagePrefix + 'flow') : null;");
        builder.AppendLine("            if (storedFlow && Array.from(flowRadios).some(radio => radio.value === storedFlow)) {");
        builder.AppendLine("                Array.from(flowRadios).forEach(radio => { radio.checked = radio.value === storedFlow; });");
        builder.AppendLine("            }");
        builder.AppendLine("            const activeFlow = Array.from(flowRadios).find(radio => radio.checked)?.value || 'application';");
        builder.AppendLine("            setFlow(activeFlow);");
        builder.AppendLine("            flowRadios.forEach(radio => {");
        builder.AppendLine("                radio.addEventListener('change', () => setFlow(radio.value));");
        builder.AppendLine("            });");
        builder.AppendLine("            const inputs = document.querySelectorAll('[data-storage-key]');");
        builder.AppendLine("            if (inputs.length > 0) {");
        builder.AppendLine("                const groups = new Map();");
        builder.AppendLine("                inputs.forEach(input => {");
        builder.AppendLine("                    const key = input.getAttribute('data-storage-key');");
        builder.AppendLine("                    if (!key) { return; }");
        builder.AppendLine("                    if (!groups.has(key)) {");
        builder.AppendLine("                        groups.set(key, []);");
        builder.AppendLine("                    }");
        builder.AppendLine("                    groups.get(key).push(input);");
        builder.AppendLine("                });");
        builder.AppendLine("                const resolveInitialValue = (key) => {");
        builder.AppendLine("                    if (hasStorage()) {");
        builder.AppendLine("                        const stored = window.localStorage.getItem(storagePrefix + key);");
        builder.AppendLine("                        if (stored !== null) { return stored; }");
        builder.AppendLine("                    }");
        builder.AppendLine("                    const peers = groups.get(key) || [];");
        builder.AppendLine("                    for (const peer of peers) {");
        builder.AppendLine("                        const initial = peer.getAttribute('data-initial-value');");
        builder.AppendLine("                        if (initial !== null) { return initial; }");
        builder.AppendLine("                    }");
        builder.AppendLine("                    return peers.length > 0 ? peers[0].value : '';"); 
        builder.AppendLine("                };");
        builder.AppendLine("                groups.forEach((elements, key) => {");
        builder.AppendLine("                    const initial = resolveInitialValue(key);");
        builder.AppendLine("                    elements.forEach(element => { element.value = initial; });");
        builder.AppendLine("                });");
        builder.AppendLine("                const persistValue = (key, value) => {");
        builder.AppendLine("                    if (hasStorage()) {");
        builder.AppendLine("                        window.localStorage.setItem(storagePrefix + key, value);");
        builder.AppendLine("                    }");
        builder.AppendLine("                    const peers = groups.get(key) || [];");
        builder.AppendLine("                    peers.forEach(peer => {");
        builder.AppendLine("                        if (peer !== document.activeElement && peer.value !== value) {");
        builder.AppendLine("                            peer.value = value;");
        builder.AppendLine("                        }");
        builder.AppendLine("                    });");
        builder.AppendLine("                };");
        builder.AppendLine("                inputs.forEach(input => {");
        builder.AppendLine("                    const key = input.getAttribute('data-storage-key');");
        builder.AppendLine("                    if (!key) { return; }");
        builder.AppendLine("                    input.addEventListener('input', () => {");
        builder.AppendLine("                        persistValue(key, input.value);");
        builder.AppendLine("                    });");
        builder.AppendLine("                });");
        builder.AppendLine("            }");
        builder.AppendLine("            const copyButtons = document.querySelectorAll('[data-copy-target]');");
        builder.AppendLine("            copyButtons.forEach(button => {");
        builder.AppendLine("                button.addEventListener('click', () => {");
        builder.AppendLine("                    const targetId = button.getAttribute('data-copy-target');");
        builder.AppendLine("                    if (!targetId) { return; }");
        builder.AppendLine("                    const target = document.getElementById(targetId);");
        builder.AppendLine("                    if (!target) { return; }");
        builder.AppendLine("                    const value = target.value || ''; ");
        builder.AppendLine("                    if (navigator.clipboard && navigator.clipboard.writeText) {");
        builder.AppendLine("                        navigator.clipboard.writeText(value).then(() => {");
        builder.AppendLine("                            const original = button.textContent;");
        builder.AppendLine("                            button.textContent = 'Copied';");
        builder.AppendLine("                            window.setTimeout(() => { button.textContent = original || 'Copy'; }, 1500);");
        builder.AppendLine("                        }).catch(() => {");
        builder.AppendLine("                            button.textContent = 'Copy failed';");
        builder.AppendLine("                            window.setTimeout(() => { button.textContent = 'Copy'; }, 1500);");
        builder.AppendLine("                        });");
        builder.AppendLine("                    } else {");
        builder.AppendLine("                        target.select();");
        builder.AppendLine("                        document.execCommand('copy');");
        builder.AppendLine("                    }");
        builder.AppendLine("                });");
        builder.AppendLine("            });");
        builder.AppendLine("        })();");
        builder.AppendLine("        (function () {");
        builder.AppendLine("            const userInput = document.getElementById('userId');");
        builder.AppendLine("            if (!userInput) { return; }");
        builder.AppendLine("            const resourceLabels = document.querySelectorAll('.resource-path[data-resource-template]');");
        builder.AppendLine("            const placeholderPattern = /\\{id\\}/gi;");
        builder.AppendLine("            const storageKey = 'graphSubscriptionUserId';");
        builder.AppendLine("            const updateLabels = () => {");
        builder.AppendLine("                const value = userInput.value.trim();");
        builder.AppendLine("                resourceLabels.forEach(label => {");
        builder.AppendLine("                    const template = label.getAttribute('data-resource-template');");
        builder.AppendLine("                    if (!template) { return; }");
        builder.AppendLine("                    label.textContent = value ? template.replace(placeholderPattern, value) : template;");
        builder.AppendLine("                });");
        builder.AppendLine("            };");
        builder.AppendLine("            const persistValue = () => {");
        builder.AppendLine("                if (!window.localStorage) { return; }");
        builder.AppendLine("                const value = userInput.value.trim();");
        builder.AppendLine("                if (value) {");
        builder.AppendLine("                    window.localStorage.setItem(storageKey, value);");
        builder.AppendLine("                } else {");
        builder.AppendLine("                    window.localStorage.removeItem(storageKey);");
        builder.AppendLine("                }");
        builder.AppendLine("            };");
        builder.AppendLine("            const restoreValue = () => {");
        builder.AppendLine("                if (!window.localStorage || userInput.value) { return; }");
        builder.AppendLine("                const stored = window.localStorage.getItem(storageKey);");
        builder.AppendLine("                if (stored) {");
        builder.AppendLine("                    userInput.value = stored;");
        builder.AppendLine("                }");
        builder.AppendLine("            };");
        builder.AppendLine("            userInput.addEventListener('input', () => {");
        builder.AppendLine("                persistValue();");
        builder.AppendLine("                updateLabels();");
        builder.AppendLine("            });");
        builder.AppendLine("            restoreValue();");
        builder.AppendLine("            updateLabels();");
        builder.AppendLine("        })();");
        builder.AppendLine("        (function () {");
        builder.AppendLine("            const refreshButton = document.getElementById('refreshEventsButton');");
        builder.AppendLine("            const autoRefreshSelect = document.getElementById('autoRefreshSelect');");
        builder.AppendLine("            if (!autoRefreshSelect) { return; }");
        builder.AppendLine("            const storageKey = 'autoRefreshIntervalSeconds';");
        builder.AppendLine("            let refreshHandle = null;");
        builder.AppendLine("            const scheduleRefresh = (seconds) => {");
        builder.AppendLine("                if (refreshHandle !== null) {");
        builder.AppendLine("                    clearInterval(refreshHandle);");
        builder.AppendLine("                    refreshHandle = null;");
        builder.AppendLine("                }");
        builder.AppendLine("                if (seconds > 0) {");
        builder.AppendLine("                    refreshHandle = window.setInterval(() => window.location.reload(), seconds * 1000);");
        builder.AppendLine("                }");
        builder.AppendLine("            };");
        builder.AppendLine("            const applySelection = (value) => {");
        builder.AppendLine("                const seconds = Number(value);");
        builder.AppendLine("                if (!Number.isFinite(seconds) || seconds < 0) {");
        builder.AppendLine("                    return;");
        builder.AppendLine("                }");
        builder.AppendLine("                if (window.localStorage) {");
        builder.AppendLine("                    window.localStorage.setItem(storageKey, seconds.toString());");
        builder.AppendLine("                }");
        builder.AppendLine("                scheduleRefresh(seconds);");
        builder.AppendLine("            };");
        builder.AppendLine("            if (refreshButton) {");
        builder.AppendLine("                refreshButton.addEventListener('click', () => window.location.reload());");
        builder.AppendLine("            }");
        builder.AppendLine("            autoRefreshSelect.addEventListener('change', () => {");
        builder.AppendLine("                applySelection(autoRefreshSelect.value);");
        builder.AppendLine("            });");
        builder.AppendLine("            let initialValue = '0';");
        builder.AppendLine("            if (window.localStorage) {");
        builder.AppendLine("                const stored = window.localStorage.getItem(storageKey);");
        builder.AppendLine("                if (stored && Array.from(autoRefreshSelect.options).some(option => option.value === stored)) {");
        builder.AppendLine("                    initialValue = stored;");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine("            autoRefreshSelect.value = initialValue;");
        builder.AppendLine("            applySelection(initialValue);");
        builder.AppendLine("        })();");
        builder.AppendLine("    </script>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static bool ResourceRequiresUserId(string? resourceTemplate) =>
        !string.IsNullOrWhiteSpace(resourceTemplate) &&
        resourceTemplate.IndexOf(UserIdPlaceholder, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string ApplyUserIdToResource(string resourceTemplate, string? userId)
    {
        if (string.IsNullOrWhiteSpace(resourceTemplate) || string.IsNullOrWhiteSpace(userId))
        {
            return resourceTemplate;
        }

        return resourceTemplate.Replace(UserIdPlaceholder, userId, StringComparison.OrdinalIgnoreCase);
    }

    private static string HtmlEncode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatDate(DateTimeOffset? dateTime) =>
        dateTime?.ToString("u", CultureInfo.InvariantCulture) ?? "-";

    private sealed class FeedbackPayload
    {
        public string[]? StatusMessages { get; init; }
        public string[]? ErrorMessages { get; init; }
    }
}
