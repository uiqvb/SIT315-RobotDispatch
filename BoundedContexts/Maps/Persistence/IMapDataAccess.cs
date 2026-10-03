using RobotControllerApi.BoundedContexts.Maps.Models;

namespace RobotControllerApi.BoundedContexts.Maps.Persistence;

public interface IMapDataAccess
{
    List<Map> GetMaps();
    Map? GetMapById(int id);
    bool MapExistsByName(string name, int? excludeId = null);
    Map InsertMap(Map newMap);
    bool UpdateMap(int id, Map updatedMap);
    bool DeleteMap(int id);

    // Async siblings for the dispatch path; the sync versions above stay for other callers.
    Task<Map?> GetMapByIdAsync(int id, CancellationToken ct = default);
}
