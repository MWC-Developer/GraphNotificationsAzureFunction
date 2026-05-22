using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace GraphNotificationsAzureFunction;

public interface ILifecycleNotificationService
{
    Task ProcessAsync(GraphNotification notification, CancellationToken cancellationToken);
}

public sealed class LifecycleNotificationService : ILifecycleNotificationService
{
    private readonly IGraphSubscriptionManager _subscriptionManager;
    private readonly ILogger<LifecycleNotificationService> _logger;

    public LifecycleNotificationService(IGraphSubscriptionManager subscriptionManager, ILogger<LifecycleNotificationService> logger)
    {
        _subscriptionManager = subscriptionManager;
        _logger = logger;
    }

    public async Task ProcessAsync(GraphNotification notification, CancellationToken cancellationToken)
    {
        if (notification is null)
        {
            return;
        }

        var lifecycleEvent = notification.LifecycleEvent;
        if (string.IsNullOrWhiteSpace(lifecycleEvent))
        {
            return;
        }

        try
        {
            switch (lifecycleEvent.ToLowerInvariant())
            {
                case "reauthorizationrequired":
                    await _subscriptionManager.ReauthorizeAsync(notification.SubscriptionId ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "subscriptionremoved":
                    await _subscriptionManager.CreateSubscriptionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "missed":
                    await _subscriptionManager.PerformFullSyncAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    _logger.LogInformation("Lifecycle event {LifecycleEvent} is not explicitly handled.", lifecycleEvent);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute lifecycle action for event {LifecycleEvent} (subscription {SubscriptionId}).", lifecycleEvent, notification.SubscriptionId);
            throw;
        }
    }
}
