using System;
using System.Collections.Generic;
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
                     (Key: "Mail", Display: "Mail"),
                     (Key: "Calendar", Display: "Calendar"),
                     (Key: "OneDrive", Display: "OneDrive")
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

            options.Add(new GraphResourceOption(template.Key.ToLowerInvariant(), template.Display, resource, changeType));
        }

        return options;
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

public sealed record GraphResourceOption(string Key, string DisplayName, string? Resource, string? ChangeType)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Resource);
}
