using Npgsql;
using RobotControllerApi.BoundedContexts.Maps.Models;
using RobotControllerApi.BoundedContexts.Maps.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.Repository;

public class MapRepository : IMapDataAccess, IRepository
{
    private readonly DbConfig _dbConfig;
    private IRepository _repo => this;

    public MapRepository(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    private string ConnectionString => _dbConfig.GetConnectionString();

    public List<Map> GetMaps()
    {
        return _repo.ExecuteReader<Map>(
            ConnectionString,
            @"SELECT id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate
              FROM public.map
              ORDER BY id;");
    }

    public Map? GetMapById(int id)
    {
        return _repo.ExecuteReader<Map>(
            ConnectionString,
            @"SELECT id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate
              FROM public.map
              WHERE id = @id;",
            new NpgsqlParameter[]
            {
                new("id", id)
            })
            .SingleOrDefault();
    }

    public bool MapExistsByName(string name, int? excludeId = null)
    {
        var sql = @"SELECT id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate
                    FROM public.map
                    WHERE lower(name) = lower(@name)";

        var parameters = new List<NpgsqlParameter>
        {
            new("name", name)
        };

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
            parameters.Add(new NpgsqlParameter("excludeId", excludeId.Value));
        }

        sql += " LIMIT 1;";

        return _repo.ExecuteReader<Map>(ConnectionString, sql, parameters.ToArray()).Any();
    }

    public Map InsertMap(Map newMap)
    {
        return _repo.ExecuteReader<Map>(
            ConnectionString,
            @"INSERT INTO public.map
              (name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate)
              VALUES (@name, @columns, @rows, @cellSizeCm, @description, @isActive, @createdDate, @modifiedDate)
              RETURNING id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate;",
            new NpgsqlParameter[]
            {
                new("name", newMap.Name),
                new("columns", newMap.Columns),
                new("rows", newMap.Rows),
                new("cellSizeCm", newMap.CellSizeCm),
                new("description", newMap.Description ?? (object)DBNull.Value),
                new("isActive", newMap.IsActive),
                new("createdDate", newMap.CreatedDate),
                new("modifiedDate", newMap.ModifiedDate)
            })
            .Single();
    }

    public bool UpdateMap(int id, Map updatedMap)
    {
        var result = _repo.ExecuteReader<Map>(
            ConnectionString,
            @"UPDATE public.map
              SET name = @name,
                  columns = @columns,
                  rows = @rows,
                  cellsizecm = @cellSizeCm,
                  description = @description,
                  isactive = @isActive,
                  modifieddate = @modifiedDate
              WHERE id = @id
              RETURNING id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate;",
            new NpgsqlParameter[]
            {
                new("id", id),
                new("name", updatedMap.Name),
                new("columns", updatedMap.Columns),
                new("rows", updatedMap.Rows),
                new("cellSizeCm", updatedMap.CellSizeCm),
                new("description", updatedMap.Description ?? (object)DBNull.Value),
                new("isActive", updatedMap.IsActive),
                new("modifiedDate", updatedMap.ModifiedDate)
            });

        return result.Any();
    }

    public bool DeleteMap(int id)
    {
        var result = _repo.ExecuteReader<Map>(
            ConnectionString,
            @"DELETE FROM public.map
              WHERE id = @id
              RETURNING id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate;",
            new NpgsqlParameter[]
            {
                new("id", id)
            });

        return result.Any();
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Map?> GetMapByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetMapById(id));
}
