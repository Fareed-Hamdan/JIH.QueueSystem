using JIH.Queue.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JIH.Queue.Data.Oracle;

/// <summary>IDataStore over one short-lived QueueDbContext (one unit of work).</summary>
public sealed class OracleDataStore(QueueDbContext db) : IDataStore
{
    public IQueryable<T> Query<T>() where T : class => db.Set<T>();
    public void Add<T>(T entity) where T : class => db.Add(entity);
    public void Remove<T>(T entity) where T : class => db.Remove(entity);
    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct = default) =>
        EntityFrameworkQueryableExtensions.ToListAsync(query, ct);
    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct = default) =>
        EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(query, ct);
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct = default) =>
        EntityFrameworkQueryableExtensions.CountAsync(query, ct);
    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct = default) =>
        EntityFrameworkQueryableExtensions.AnyAsync(query, ct);

    public ValueTask DisposeAsync() => db.DisposeAsync();
}

public sealed class OracleDataStoreFactory(IDbContextFactory<QueueDbContext> contexts, ILogger<OracleDataStoreFactory> logger) : IDataStoreFactory
{
    public IDataStore Create() => new OracleDataStore(contexts.CreateDbContext());

    /// <summary>Creates the tables on first start. Retries while the database container is still starting.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await contexts.CreateDbContextAsync(ct);
                var created = await db.Database.EnsureCreatedAsync(ct);
                logger.LogInformation(created ? "Oracle schema created." : "Oracle schema already exists.");
                return;
            }
            catch (Exception ex) when (attempt < 30 && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Oracle not ready (attempt {Attempt}/30): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }
}

public static class OracleStorageRegistration
{
    /// <summary>Registers Oracle storage. Connection string example:
    /// "User Id=JIH_QUEUE;Password=...;Data Source=oracle-db:1521/FREEPDB1"</summary>
    public static IServiceCollection AddOracleQueueStorage(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<QueueDbContext>(o => o.UseOracle(connectionString));
        services.AddSingleton<IDataStoreFactory, OracleDataStoreFactory>();
        return services;
    }
}
