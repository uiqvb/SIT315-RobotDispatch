using RobotControllerApi.BoundedContexts.Maps.Models;
using RobotControllerApi.BoundedContexts.Maps.Persistence;

namespace RobotControllerApi.Infrastructure.DataAccess.EFCore;

public class MapEF : IMapDataAccess
{
    private readonly RobotContext _context;

    public MapEF(RobotContext context)
    {
        _context = context;
    }

    public List<Map> GetMaps()
    {
        return _context.Maps
            .OrderBy(x => x.Id)
            .ToList();
    }

    public Map? GetMapById(int id)
    {
        return _context.Maps
            .SingleOrDefault(x => x.Id == id);
    }

    public bool MapExistsByName(string name, int? excludeId = null)
    {
        var query = _context.Maps
            .Where(x => x.Name.ToUpper() == name.ToUpper());

        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }

        return query.Any();
    }

    public Map InsertMap(Map newMap)
    {
        _context.Maps.Add(newMap);
        _context.SaveChanges();
        return newMap;
    }

    public bool UpdateMap(int id, Map updatedMap)
    {
        var existingMap = _context.Maps
            .SingleOrDefault(x => x.Id == id);

        if (existingMap == null)
        {
            return false;
        }

        existingMap.Name = updatedMap.Name;
        existingMap.Columns = updatedMap.Columns;
        existingMap.Rows = updatedMap.Rows;
        existingMap.CellSizeCm = updatedMap.CellSizeCm;
        existingMap.Description = updatedMap.Description;
        existingMap.IsActive = updatedMap.IsActive;
        existingMap.ModifiedDate = updatedMap.ModifiedDate;

        _context.SaveChanges();
        return true;
    }

    public bool DeleteMap(int id)
    {
        var existingMap = _context.Maps
            .SingleOrDefault(x => x.Id == id);

        if (existingMap == null)
        {
            return false;
        }

        _context.Maps.Remove(existingMap);
        _context.SaveChanges();
        return true;
    }

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Map?> GetMapByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetMapById(id));
}
