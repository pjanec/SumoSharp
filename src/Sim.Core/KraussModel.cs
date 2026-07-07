using Sim.Ingest;

namespace Sim.Core;

// Ported from sumo/src/microsim/cfmodels/MSCFModel.cpp, MSCFModel_KraussOrig1.cpp and
// MSCFModel_Krauss.cpp. CLAUDE.md rule 1: this is a straight port of what those files DO, not
// a re-derivation from a paper -- read them again before touching this file.
//
// Unit macros (sumo/src/utils/common/SUMOTime.h) are kept as explicit functions taking dt
// rather than hard-coded constants, so this generalizes past dt=1 (rung 1's step-length):
//   ACCEL2SPEED(a) = a*TS   SPEED2DIST(v) = v*TS   DIST2SPEED(d) = d/TS
// TS there is the *simulation* step-length in seconds; we thread dt through explicitly instead
// of relying on a global.
public static class KraussModel
{
    public static double Accel2Speed(double accel, double dt) => accel * dt;

    public static double Speed2Dist(double speed, double dt) => speed * dt;

    public static double Dist2Speed(double dist, double dt) => dist / dt;

    // MSCFModel.cpp: maxNextSpeed(speed, veh) =
    //   MIN2(speed + ACCEL2SPEED(getMaxAccel()), myType->getMaxSpeed())
    public static double MaxNextSpeed(double speed, ResolvedVType vType, double dt) =>
        Math.Min(speed + Accel2Speed(vType.Accel, dt), vType.MaxSpeed);

    // MSCFModel.cpp: minNextSpeed(speed, veh), Euler branch (gSemiImplicitEulerUpdate) --
    // phase 1 is Euler-only per CLAUDE.md/DESIGN.md (Ballistic support is a later task).
    //   return MAX2(speed - ACCEL2SPEED(myDecel), 0.);
    public static double MinNextSpeed(double speed, ResolvedVType vType, double dt) =>
        Math.Max(speed - Accel2Speed(vType.Decel, dt), 0.0);

    // MSCFModel.cpp: minNextSpeedEmergency(speed, veh), Euler branch --
    //   return MAX2(speed - ACCEL2SPEED(myEmergencyDecel), 0.);
    public static double MinNextSpeedEmergency(double speed, ResolvedVType vType, double dt) =>
        Math.Max(speed - Accel2Speed(vType.EmergencyDecel, dt), 0.0);

    // MSCFModel_KraussOrig1.cpp: vsafe(gap, predSpeed, predMaxDecel). This is the exact leader
    // safe-speed formula, including the two guard cases at the top. Rung 1 has NO leader, so
    // the caller passes gap=+infinity, which short-circuits to +infinity here (a non-binding
    // constraint) -- this method is dead-but-present, wired for a real leader once one exists
    // (rung 4+). predMaxDecel is accepted for call-shape parity with the source but is unused
    // by this (KraussOrig1) vsafe formula, exactly as in the vendored C++.
    public static double VSafe(double gap, double predSpeed, double predMaxDecel, ResolvedVType vType, double dt)
    {
        _ = predMaxDecel; // unused by KraussOrig1::vsafe, kept for signature parity with C++

        if (double.IsPositiveInfinity(gap))
        {
            return double.PositiveInfinity;
        }

        if (predSpeed == 0 && gap < 0.01)
        {
            return 0.0;
        }

        var decelAccelDist = Accel2Speed(vType.Decel, dt); // ACCEL2SPEED(myDecel)
        if (predSpeed == 0 && gap <= decelAccelDist)
        {
            // workaround for #2310
            return Math.Min(decelAccelDist, Dist2Speed(gap, dt));
        }

        var tauDecel = vType.Decel * vType.Tau; // myTauDecel = myDecel * myHeadwayTime
        var vsafe = -tauDecel + Math.Sqrt((tauDecel * tauDecel) + (predSpeed * predSpeed) + (2.0 * vType.Decel * gap));
        return vsafe;
    }

    // MSLane.h getVehicleMaxSpeed (no-restriction branch): MIN2(veh->getMaxSpeed(), laneSpeed *
    // veh->getChosenSpeedFactor()). This is the "desired free-flow speed" constraint fed into
    // the leader/junction/stop-line reducer as the no-obstruction case.
    public static double LaneVehicleMaxSpeed(double laneSpeed, ResolvedVType vType) =>
        Math.Min(laneSpeed * vType.SpeedFactor, vType.MaxSpeed);

    // MSCFModel.cpp: finalizeSpeed(veh, vPos). vPos is the MIN over the (already-reduced)
    // leader/junction/stop-line constraint collection computed by the caller; laneVehicleMaxSpeed
    // is the lane's speed limit adaptation for this vehicle (MSLane::getVehicleMaxSpeed).
    //
    // vStop / stop-line handling (processNextStop) is not modeled yet in phase 1 -- like the
    // leader vsafe constraint, it is a dead-but-present +infinity slot (no stops exist in rung
    // 1's scenario) rather than removed, so wiring a stop line in later rungs is additive.
    public static double FinalizeSpeed(
        double oldV,
        double vPos,
        double laneVehicleMaxSpeed,
        ResolvedVType vType,
        double dt,
        double actionStepLengthSecs)
    {
        var vStop = Math.Min(vPos, double.PositiveInfinity); // processNextStop(vPos) -- no stop modeled yet

        var vMinEmergency = MinNextSpeedEmergency(oldV, vType, dt);
        // vMin = MIN2(minNextSpeed(oldV), MAX2(vPos, vMinEmergency))
        var vMin = Math.Min(MinNextSpeed(oldV, vType, dt), Math.Max(vPos, vMinEmergency));

        // getFriction()==1 in phase 1 (no weather/friction model) -> factor == 1.
        const double factor = 1.0;

        var aMax = ((Math.Max(laneVehicleMaxSpeed, vPos) * factor) - oldV) / actionStepLengthSecs;
        var vMax = Min3(oldV + Accel2Speed(aMax, dt), MaxNextSpeed(oldV, vType, dt), vStop);
        vMax = Math.Max(vMin, vMax);

        // sigma=0 in rung 1 => patchSpeedBeforeLC (dawdle) is a no-op; no lane-change model and
        // startupDelay defaults to 0 => applyStartupDelay is a no-op too. So vNext = vMax.
        var vNext = vMax;
        return vNext;
    }

    private static double Min3(double a, double b, double c) => Math.Min(a, Math.Min(b, c));
}
