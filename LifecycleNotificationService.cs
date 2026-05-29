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
                    // The Graph docs are explicit: POST /reauthorize satisfies the auth challenge
                    // but does NOT extend the subscription lifetime — the subscription will still
                    // expire at its original time and be removed by Graph.
                    // PATCH /subscriptions/{id} with a new expirationDateTime performs both
                    // reauthorization and renewal in a single request. The docs also warn never to
                    // call /reauthorize and PATCH within the same 10-minute window, so we do only
                    // the PATCH here.
                    _logger.LogInformation(
                        "Lifecycle reauthorizationRequired received for subscription {SubscriptionId} (current expiry {Expiration}). Initiating renewal via PATCH.",
                        notification.SubscriptionId,
                        notification.SubscriptionExpirationDateTime);
                    await _subscriptionManager.RenewSubscriptionAsync(notification.SubscriptionId ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case "subscriptionremoved":
                    _logger.LogInformation(
                        "Lifecycle subscriptionRemoved received for subscription {SubscriptionId}. Recreating subscription.",
                        notification.SubscriptionId);
                    await _subscriptionManager.CreateSubscriptionAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case "missed":
                    _logger.LogInformation(
                        "Lifecycle missed received for subscription {SubscriptionId}. Triggering full data sync.",
                        notification.SubscriptionId);
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
