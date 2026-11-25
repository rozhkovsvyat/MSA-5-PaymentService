using StackExchange.Redis;

namespace FraudCheckService.Checks;

/// <summary>
/// Последний известный результат AML-скрининга пары клиент–продавец в Redis. Нужен, когда AML-провайдер
/// не ответил в срок. Redis недоступен — последнего результата просто нет, платёж из-за этого не падает.
/// </summary>
public sealed class AmlScreeningCache(IConnectionMultiplexer redis, TimeSpan ttl, ILogger<AmlScreeningCache> log)
{
    public async Task<string?> GetAsync(string clientId, string counterpartyId)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(clientId, counterpartyId));
            return value.HasValue ? value.ToString() : null;
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("кеш AML недоступен ({Reason}): последнего результата скрининга {ClientId} → {CounterpartyId} нет",
                Short(e), clientId, counterpartyId);
            return null;
        }
    }

    public async Task PutAsync(string clientId, string counterpartyId, string decision)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(clientId, counterpartyId), decision, ttl);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            log.LogWarning("кеш AML недоступен ({Reason}): результат скрининга {ClientId} → {CounterpartyId} не сохранён",
                Short(e), clientId, counterpartyId);
        }
    }

    private static string Key(string clientId, string counterpartyId) => $"aml:{clientId}:{counterpartyId}";

    private static string Short(Exception e) => e.GetType().Name;
}
