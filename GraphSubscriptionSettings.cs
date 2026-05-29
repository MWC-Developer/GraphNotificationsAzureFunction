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

using Microsoft.Extensions.Configuration;

namespace GraphNotificationsAzureFunction;

public sealed class GraphSubscriptionSettings
{
    private const string DefaultScope = "https://graph.microsoft.com/.default";
    private const string DefaultBaseUrl = "https://graph.microsoft.com/v1.0";

    public GraphSubscriptionSettings(IConfiguration configuration)
    {
        TenantId = ResolveSetting(configuration, "Graph:TenantId", "GraphSubscription:TenantId", "GraphTenantId");
        ClientId = ResolveSetting(configuration, "Graph:ClientId", "GraphSubscription:ClientId", "GraphClientId");
        ClientSecret = ResolveSetting(configuration, "Graph:ClientSecret", "GraphSubscription:ClientSecret", "GraphClientSecret");
        ChangeType = ResolveSetting(configuration, "Graph:ChangeType", "GraphSubscription:ChangeType", "GraphSubscriptionChangeType");
        Resource = ResolveSetting(configuration, "Graph:Resource", "GraphSubscription:Resource", "GraphSubscriptionResource");
        NotificationUrl = ResolveSetting(configuration, "Graph:NotificationUrl", "GraphSubscription:NotificationUrl", "GraphNotificationUrl")
            ?? BuildUrlFromHost("graph/notifications");
        LifecycleNotificationUrl = ResolveSetting(configuration, "Graph:LifecycleNotificationUrl", "GraphSubscription:LifecycleNotificationUrl", "GraphLifecycleNotificationUrl")
            ?? NotificationUrl
            ?? BuildUrlFromHost("graph/lifecycle");
        ClientState = ResolveSetting(configuration, "Graph:ClientState", "GraphSubscription:ClientState", "GraphClientState");

        var lifetimeMinutes = ResolveSetting(configuration, "Graph:SubscriptionLifetimeMinutes", "GraphSubscription:SubscriptionLifetimeMinutes", "GraphSubscriptionLifetimeMinutes");
        if (int.TryParse(lifetimeMinutes, out var parsedMinutes) && parsedMinutes > 0)
        {
            SubscriptionLifetime = TimeSpan.FromMinutes(parsedMinutes);
        }
        else
        {
            SubscriptionLifetime = TimeSpan.FromMinutes(60);
        }

        Scope = ResolveSetting(configuration, "Graph:Scope", "GraphSubscription:Scope", "GraphScope") ?? DefaultScope;
        GraphBaseUrl = ResolveSetting(configuration, "Graph:BaseUrl", "GraphSubscription:BaseUrl", "GraphBaseUrl") ?? DefaultBaseUrl;

        ResourceOptions = BuildResourceOptions(configuration);
    }

    public string? TenantId { get; }
    public string? ClientId { get; }
    public string? ClientSecret { get; }
    public string? ChangeType { get; }
    public string? Resource { get; }
    public string? NotificationUrl { get; }
    public string? LifecycleNotificationUrl { get; }
    public string? ClientState { get; }
    public TimeSpan SubscriptionLifetime { get; }
    public string Scope { get; }
    public string GraphBaseUrl { get; }
    public IReadOnlyList<GraphResourceOption> ResourceOptions { get; }

    public bool IsConfigured => GetMissingConfigurationFields().Count == 0;

    public IReadOnlyList<string> GetMissingConfigurationFields()
    {
        var missing = new List<string>();
        AddFieldIfMissing(missing, TenantId, "Graph:TenantId / GraphSubscription:TenantId / GraphTenantId");
        AddFieldIfMissing(missing, ClientId, "Graph:ClientId / GraphSubscription:ClientId / GraphClientId");
        AddFieldIfMissing(missing, ClientSecret, "Graph:ClientSecret / GraphSubscription:ClientSecret / GraphClientSecret");
        AddFieldIfMissing(missing, NotificationUrl, "Graph:NotificationUrl / GraphSubscription:NotificationUrl / GraphNotificationUrl");
        AddFieldIfMissing(missing, LifecycleNotificationUrl, "Graph:LifecycleNotificationUrl / GraphSubscription:LifecycleNotificationUrl / GraphLifecycleNotificationUrl");
        return missing;
    }

    private IReadOnlyList<GraphResourceOption> BuildResourceOptions(IConfiguration configuration)
    {
        var options = new List<GraphResourceOption>();

        if (!string.IsNullOrWhiteSpace(Resource))
        {
            options.Add(new GraphResourceOption("default", "Default", Resource, ChangeType));
        }

        foreach (var template in new[]
                 {
                     (Key: "Mail", Display: "Mail", Category: "Microsoft 365"),
                     (Key: "Calendar", Display: "Calendar", Category: "Microsoft 365"),
                     (Key: "OneDrive", Display: "OneDrive", Category: "Microsoft 365")
                 })
        {
            var resource = ResolveSetting(configuration,
                $"Graph:Resources:{template.Key}",
                $"GraphSubscription:Resources:{template.Key}",
                $"GraphResource{template.Key}",
                $"GraphSubscriptionResource{template.Key}");

            var changeType = ResolveSetting(configuration,
                $"Graph:Resources:{template.Key}ChangeType",
                $"GraphSubscription:Resources:{template.Key}ChangeType",
                $"GraphResource{template.Key}ChangeType",
                $"GraphSubscriptionResource{template.Key}ChangeType") ?? ChangeType;

            options.Add(new GraphResourceOption(template.Key.ToLowerInvariant(), template.Display, resource, changeType, template.Category));
        }

        AddTeamsResourceOptions(options, configuration);

        return options;
    }

    private void AddTeamsResourceOptions(IList<GraphResourceOption> options, IConfiguration configuration)
    {
        // Teams resource templates: key, display name, default resource path, default change type
        var teamsTemplates = new[]
        {
            (Key: "TeamsTeam",          Display: "Teams — Teams (all)",          DefaultResource: "/teams",                                                          DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsTeamById",      Display: "Teams — Specific Team",         DefaultResource: "/teams/{id}",                                                     DefaultChangeType: "updated,deleted"),
            (Key: "TeamsChannel",       Display: "Teams — Channels (all teams)",  DefaultResource: "/teams/getAllChannels",                                            DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChannelById",   Display: "Teams — Channels (one team)",   DefaultResource: "/teams/{id}/channels",                                            DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChat",          Display: "Teams — Chats (all)",           DefaultResource: "/chats",                                                          DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChatById",      Display: "Teams — Specific Chat",         DefaultResource: "/chats/{id}",                                                     DefaultChangeType: "updated,deleted"),
            (Key: "TeamsChatByUser",    Display: "Teams — Chats (user)",          DefaultResource: "/users/{id}/chats",                                               DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChatMessage",   Display: "Teams — Messages (all chats)",  DefaultResource: "/chats/getAllMessages",                                            DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChatMessageByChat", Display: "Teams — Messages (one chat)", DefaultResource: "/chats/{id}/messages",                                          DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChannelMessage",Display: "Teams — Messages (all channels)", DefaultResource: "/teams/getAllMessages",                                          DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsChannelMessageByTeam", Display: "Teams — Messages (one channel)", DefaultResource: "/teams/{id}/channels/{id}/messages",                       DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsMember",        Display: "Teams — Members (team)",        DefaultResource: "/teams/{id}/members",                                             DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsMemberChat",    Display: "Teams — Members (chat)",        DefaultResource: "/chats/{id}/members",                                             DefaultChangeType: "created,updated,deleted"),
            (Key: "TeamsCallRecord",    Display: "Teams — Call Records",          DefaultResource: "/communications/callRecords",                                     DefaultChangeType: "created,updated"),
            (Key: "TeamsCallRecording", Display: "Teams — Call Recordings (org)", DefaultResource: "/communications/onlineMeetings/getAllRecordings",                  DefaultChangeType: "created"),
            (Key: "TeamsCallRecordingByUser", Display: "Teams — Call Recordings (user)", DefaultResource: "/users/{id}/onlineMeetings/getAllRecordings",              DefaultChangeType: "created"),
            (Key: "TeamsCallTranscript",Display: "Teams — Transcripts (org)",     DefaultResource: "/communications/onlineMeetings/getAllTranscripts",                 DefaultChangeType: "created"),
            (Key: "TeamsCallTranscriptByUser", Display: "Teams — Transcripts (user)", DefaultResource: "/users/{id}/onlineMeetings/getAllTranscripts",                DefaultChangeType: "created"),
            (Key: "TeamsPresence",      Display: "Teams — Presence (user)",       DefaultResource: "/communications/presences/{id}",                                  DefaultChangeType: "updated"),
            (Key: "TeamsApproval",      Display: "Teams — Approvals",             DefaultResource: "/solutions/approval/approvalItems",                               DefaultChangeType: "created,updated,deleted"),
        };

        foreach (var template in teamsTemplates)
        {
            var resource = ResolveSetting(configuration,
                $"Graph:Resources:{template.Key}",
                $"GraphSubscription:Resources:{template.Key}",
                $"GraphResource{template.Key}") ?? template.DefaultResource;

            var changeType = ResolveSetting(configuration,
                $"Graph:Resources:{template.Key}ChangeType",
                $"GraphSubscription:Resources:{template.Key}ChangeType",
                $"GraphResource{template.Key}ChangeType") ?? template.DefaultChangeType;

            options.Add(new GraphResourceOption(template.Key.ToLowerInvariant(), template.Display, resource, changeType, "Microsoft Teams"));
        }
    }

    private static string? ResolveSetting(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AddFieldIfMissing(ICollection<string> accumulator, string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            accumulator.Add(description);
        }
    }

    private static string? BuildUrlFromHost(string relativePath)
    {
        var host = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var normalizedHost = host.Trim().TrimEnd('/');
        var hasScheme = normalizedHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        normalizedHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var baseUrl = hasScheme ? normalizedHost : $"https://{normalizedHost}";
        var normalizedPath = relativePath.TrimStart('/');
        return $"{baseUrl}/api/{normalizedPath}";
    }
}

public sealed record GraphResourceOption(string Key, string DisplayName, string? Resource, string? ChangeType, string Category = "General")
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Resource);
}
