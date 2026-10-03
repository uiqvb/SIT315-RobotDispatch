using RobotControllerApi.BoundedContexts.Maps.Models;
using RobotControllerApi.BoundedContexts.Maps.Persistence;

namespace RobotControllerApi.Tests.Unit;

// A hand-written stand-in for the persistence layer, so MapService's validation can be
// exercised as a unit: no database, no HTTP, no mocking framework. It stores what it is
// given and hands it back, which is all these tests need of it.
internal sealed class InMemoryMapDataAccess : IMapDataAccess
{
    private readonly Dictionary<int, Map> _maps = new();
    private int _nextId = 1;

    public List<Map> GetMaps() => _maps.Values.ToList();

    public Map? GetMapById(int id) => _maps.TryGetValue(id, out var map) ? map : null;

    public bool MapExistsByName(string name, int? excludeId = null) =>
        _maps.Values.Any(m => m.Id != excludeId && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public Map InsertMap(Map newMap)
    {
        newMap.Id = _nextId++;
        _maps[newMap.Id] = newMap;
        return newMap;
    }

    public bool UpdateMap(int id, Map updatedMap)
    {
        if (!_maps.ContainsKey(id)) return false;
        _maps[id] = updatedMap;
        return true;
    }

    public bool DeleteMap(int id) => _maps.Remove(id);

    // Not the configured provider: async siblings return the sync result, no real async I/O here.
    public Task<Map?> GetMapByIdAsync(int id, CancellationToken ct = default) => Task.FromResult(GetMapById(id));
}
