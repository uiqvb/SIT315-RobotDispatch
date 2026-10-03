using Npgsql;
using RobotControllerApi.BoundedContexts.Maps.Models;
using RobotControllerApi.BoundedContexts.Maps.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.ADO;

public class MapADO : IMapDataAccess
{
    private readonly DbConfig _dbConfig;

    public MapADO(DbConfig dbConfig)
    {
        _dbConfig = dbConfig;
    }

    public List<Map> GetMaps()
    {
        var results = new List<Map>();

        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"SELECT id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate
              FROM map
              ORDER BY id;", conn);

        using var dr = cmd.ExecuteReader();

        while (dr.Read())
        {
            results.Add(MapMap(dr));
        }

        return results;
    }

    private const string GetMapByIdSql =
        @"SELECT id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate
              FROM map
              WHERE id = @id;";

    public Map? GetMapById(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(GetMapByIdSql, conn);

        cmd.Parameters.AddWithValue("@id", id);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapMap(dr);
        }

        return null;
    }

    public Task<Map?> GetMapByIdAsync(int id, CancellationToken ct = default) =>
        AdoAsync.QuerySingleAsync(_dbConfig, GetMapByIdSql, cmd => cmd.Parameters.AddWithValue("@id", id), MapMap, ct);

    public bool MapExistsByName(string name, int? excludeId = null)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        var sql = @"SELECT id
                    FROM map
                    WHERE lower(name) = lower(@name)";

        if (excludeId.HasValue)
        {
            sql += " AND id <> @excludeId";
        }

        sql += " LIMIT 1;";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);

        if (excludeId.HasValue)
        {
            cmd.Parameters.AddWithValue("@excludeId", excludeId.Value);
        }

        using var dr = cmd.ExecuteReader();
        return dr.Read();
    }

    public Map InsertMap(Map newMap)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"INSERT INTO map
              (name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate)
              VALUES (@name, @columns, @rows, @cellSizeCm, @description, @isActive, @createdDate, @modifiedDate)
              RETURNING id, name, columns, rows, cellsizecm, description, isactive, createddate, modifieddate;", conn);

        cmd.Parameters.AddWithValue("@name", newMap.Name);
        cmd.Parameters.AddWithValue("@columns", newMap.Columns);
        cmd.Parameters.AddWithValue("@rows", newMap.Rows);
        cmd.Parameters.AddWithValue("@cellSizeCm", newMap.CellSizeCm);
        cmd.Parameters.AddWithValue("@description", newMap.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isActive", newMap.IsActive);
        cmd.Parameters.AddWithValue("@createdDate", newMap.CreatedDate);
        cmd.Parameters.AddWithValue("@modifiedDate", newMap.ModifiedDate);

        using var dr = cmd.ExecuteReader();

        if (dr.Read())
        {
            return MapMap(dr);
        }

        throw new InvalidOperationException("Map insert did not return a row.");
    }

    public bool UpdateMap(int id, Map updatedMap)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"UPDATE map
              SET name = @name,
                  columns = @columns,
                  rows = @rows,
                  cellsizecm = @cellSizeCm,
                  description = @description,
                  isactive = @isActive,
                  modifieddate = @modifiedDate
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", updatedMap.Name);
        cmd.Parameters.AddWithValue("@columns", updatedMap.Columns);
        cmd.Parameters.AddWithValue("@rows", updatedMap.Rows);
        cmd.Parameters.AddWithValue("@cellSizeCm", updatedMap.CellSizeCm);
        cmd.Parameters.AddWithValue("@description", updatedMap.Description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isActive", updatedMap.IsActive);
        cmd.Parameters.AddWithValue("@modifiedDate", updatedMap.ModifiedDate);

        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteMap(int id)
    {
        using var conn = new NpgsqlConnection(_dbConfig.GetConnectionString());
        conn.Open();

        using var cmd = new NpgsqlCommand(
            @"DELETE FROM map
              WHERE id = @id;", conn);

        cmd.Parameters.AddWithValue("@id", id);

        return cmd.ExecuteNonQuery() > 0;
    }

    private static Map MapMap(NpgsqlDataReader dr)
    {
        return new Map
        {
            Id = dr.GetInt32(0),
            Name = dr.GetString(1),
            Columns = dr.GetInt32(2),
            Rows = dr.GetInt32(3),
            CellSizeCm = dr.GetDouble(4),
            Description = dr.IsDBNull(5) ? null : dr.GetString(5),
            IsActive = dr.GetBoolean(6),
            CreatedDate = dr.GetDateTime(7),
            ModifiedDate = dr.GetDateTime(8)
        };
    }
}
