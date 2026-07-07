namespace Sim.Core;

// The engine seam (DESIGN.md "build order"): implemented starting Task 2. Not implemented here.
public interface IEngine
{
    void LoadScenario(string netXmlPath, string rouXmlPath, string sumocfgPath);

    TrajectorySet Run(int steps);

    // B1 external-obstacle input surface (DESIGN.md "Two futures"): a live object (non-SUMO vehicle,
    // pedestrian, detection) injected between the offline model and the reducer. Inert-when-absent.
    void AddObstacle(string id, string laneId, double frontPos, double length,
                     double startTime = double.NegativeInfinity, double endTime = double.PositiveInfinity);
    void RemoveObstacle(string id);
    void ClearObstacles();
}
