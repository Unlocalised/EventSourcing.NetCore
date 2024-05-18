using AutoMapper;
using AutoMapper.QueryableExtensions;
using ECommerce.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ECommerce.Domain.Core.Services;

public abstract class ReadOnlyOnlyService<TEntity>(IQueryable<TEntity> query, IMapper mapper): IReadOnlyService<TEntity>
    where TEntity : class, IEntity, new()
{
    public async Task<TResponse> GetByIdAsync<TResponse>(Guid id, CancellationToken ct)
    {
        return mapper.Map<TResponse>(await GetEntityByIdAsync(id, ct));
    }

    public Task<List<TResponse>> GetPagedAsync<TResponse>(CancellationToken ct, int pageNumber = 1, int pageSize = 20)
    {
        return Query()
            .Skip(pageNumber * pageSize)
            .Take(pageSize)
            .ProjectTo<TResponse>(mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }
    public IQueryable<TEntity> Query()
    {
        return query;
    }

    protected async Task<TEntity> GetEntityByIdAsync(Guid id, CancellationToken ct)
    {
        var result = await query.SingleOrDefaultAsync(e => e.Id == id, ct);

        if (result == null)
            throw new ArgumentException($"{typeof(TEntity).Name} with id '{id}' was not found");

        return result;
    }
}
