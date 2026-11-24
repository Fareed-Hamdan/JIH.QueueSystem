namespace JIH.Queue.Core.Data;

/// <summary>
/// Minimal unit-of-work abstraction over the database.
/// Implemented by the Oracle EF Core store (production) and the in-memory store (development / tests).
///
/// Rules for code using it:
///  * Validate first, mutate after – the in-memory store applies changes immediately.
///  * Call SaveChangesAsync after Add() before relying on the generated Id.
///  * Do not rely on queries returning entities added in the same unit of work before saving.
/// </summary>
public interface IDataStore : IAsyncDisposable
{
    IQueryable<T> Query<T>() where T : class;
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;
    Task SaveChangesAsync(CancellationToken ct = default);

    // Async materialisation helpers (EF uses its async operators, in-memory runs synchronously).
    Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct = default);
    Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct = default);
    Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct = default);
}

public interface IDataStoreFactory
{
    IDataStore Create();

    /// <summary>Creates the schema if needed (EnsureCreated for Oracle). Called once at startup.</summary>
    Task InitializeAsync(CancellationToken ct = default);
}

public static class DataStoreExtensions
{
    public static void RemoveRange<T>(this IDataStore store, IEnumerable<T> items) where T : class
    {
        foreach (var item in items.ToList()) store.Remove(item);
    }
}
