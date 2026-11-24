using System.Collections;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Data;

/// <summary>
/// Process-wide in-memory database used for development, demos without Oracle, and the rule tests.
/// All access is serialised by one async-reentrant lock, which is fine for a single server.
/// Data is lost when the process stops.
/// </summary>
public sealed class InMemoryDatabase
{
    internal readonly Dictionary<Type, IList> Tables = new();
    internal readonly Dictionary<Type, int> Identity = new();
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal readonly AsyncLocal<int> Depth = new();

    internal List<T> Table<T>()
    {
        if (!Tables.TryGetValue(typeof(T), out var list))
        {
            list = new List<T>();
            Tables[typeof(T)] = list;
        }
        return (List<T>)list;
    }

    internal int NextId(Type t)
    {
        Identity.TryGetValue(t, out var current);
        Identity[t] = ++current;
        return current;
    }
}

public sealed class InMemoryDataStoreFactory(InMemoryDatabase db) : IDataStoreFactory
{
    public IDataStore Create() => new InMemoryDataStore(db);
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class InMemoryDataStore : IDataStore
{
    private readonly InMemoryDatabase _db;
    private bool _disposed;

    public InMemoryDataStore(InMemoryDatabase db)
    {
        _db = db;
        // Re-entrant lock: nested stores in the same async flow do not deadlock.
        if (_db.Depth.Value == 0) _db.Gate.Wait();
        _db.Depth.Value++;
    }

    public IQueryable<T> Query<T>() where T : class => _db.Table<T>().ToList().AsQueryable();

    public void Add<T>(T entity) where T : class
    {
        if (entity is IEntity e && e.Id == 0) e.Id = _db.NextId(typeof(T));
        _db.Table<T>().Add(entity);
    }

    public void Remove<T>(T entity) where T : class => _db.Table<T>().Remove(entity);

    public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct = default) => Task.FromResult(query.ToList());
    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct = default) => Task.FromResult(query.FirstOrDefault());
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct = default) => Task.FromResult(query.Count());
    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct = default) => Task.FromResult(query.Any());

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _db.Depth.Value--;
        if (_db.Depth.Value == 0) _db.Gate.Release();
        return ValueTask.CompletedTask;
    }
}
