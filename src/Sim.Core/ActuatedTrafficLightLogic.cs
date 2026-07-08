using Sim.Ingest;

namespace Sim.Core;

// C6-ii: a stateful, detector-driven traffic-light program -- ported from
// sumo/src/microsim/traffic_lights/MSActuatedTrafficLightLogic.cpp (the DEFAULT gap-based
// algorithm) + its induction-loop detector model (sumo/src/microsim/output/MSInductLoop.cpp).
//
// Unlike a 'static' program (Sim.Core.TrafficLightState, a pure function of time), an actuated
// program is STATEFUL: each green phase EXTENDS while a downstream induction loop keeps detecting
// vehicles within `max-gap` seconds, and ENDS (advancing to the next phase) once the gap opens,
// bounded by the phase's [minDur, maxDur] window. The yellow/all-red phases in between carry a
// single fixed duration (minDur == maxDur) and are never extended.
//
// This class owns BOTH the per-TLS phase-machine state (Step/LastSwitch/NextSwitchTime) and the
// detector state (per-lane LastLeaveTime / occupancy). The Engine drives it with two calls per
// step:
//   * Advance(evalTime)  -- BEFORE PlanMovements: runs trySwitch for every switch event due by
//     evalTime, using detector state settled by the PREVIOUS step's moves. Sets Step so
//     RedLightConstraint reads the correct phase.
//   * NotifyMove(...)    -- inside ExecuteMoves, once per (vehicle on a detector lane): feeds the
//     induction loops the vehicle's within-step motion so their LastLeaveTime advances exactly as
//     MSInductLoop::notifyMove computes it.
//
// SCOPE (documented, all faithful to the source for what the committed anchor exercises; the
// unexercised general machinery is deliberately not ported and is called out inline):
//   * default gap-based algorithm only -- no switching-rules / conditions / assignments /
//     multi-target next-phase / TraCI overrides (MSActuatedTrafficLightLogic's mySwitchingRules,
//     decideNextPhase, myConditions, etc.).
//   * jam detection disabled (no jam-threshold param) -- isJammed() is always false.
//   * detector-to-phase assignment covers green-MAJOR links; the greenMinor check1a-f filtering
//     (one-lane / weak-conflict admittance) is not ported (the anchor's green links are all
//     major). detector-length 0, single induction loop per relevant incoming lane.
//   * nonzero `offset` for an actuated program is not handled (the anchor's offset is 0).
public sealed class ActuatedTrafficLightLogic
{
    // MSActuatedTrafficLightLogic.cpp:58-64 default parameters.
    private const double DefaultMaxGap = 3.0;
    private const double DefaultPassingTime = 1.9;
    private const double DefaultDetectorGap = 2.0;
    private const double DefaultLengthWithGap = 7.5;

    private readonly TlLogic _logic;
    private readonly double _begin;
    private readonly IReadOnlyList<Detector> _detectors;
    // _loopsForPhase[p] = the detectors whose gaps gate phase p's extension (empty for a
    // non-actuated phase). Parallel to _logic.Phases.
    private readonly IReadOnlyList<IReadOnlyList<Detector>> _loopsForPhase;

    // --- mutable phase-machine state (MSSimpleTrafficLightLogic::myStep + myPhases[]->myLastSwitch,
    //     plus the scheduled SwitchCommand time). Reset() restores initial values. ---
    public int Step { get; private set; }
    private double _lastSwitch;
    private double _nextSwitchTime;

    private ActuatedTrafficLightLogic(
        TlLogic logic, double begin,
        IReadOnlyList<Detector> detectors,
        IReadOnlyList<IReadOnlyList<Detector>> loopsForPhase)
    {
        _logic = logic;
        _begin = begin;
        _detectors = detectors;
        _loopsForPhase = loopsForPhase;
        Reset();
    }

    // The current phase's link-state string (getCurrentPhaseDef().getState()), read by
    // RedLightConstraint at each link index.
    public string CurrentState => _logic.Phases[Step].State;

    // Time the current phase has been active -- the actuated analog of GetPhaseElapsed, used only
    // by MSVehicle::ignoreRed (jmDriveAfterRedTime), a no-op for any vType that doesn't set it.
    public double PhaseElapsed(double time) => time - _lastSwitch;

    // Restores the initial phase-machine + detector state (called at the start of every Run()).
    // NLJunctionControlBuilder.cpp:297: an actuated program's first switch is scheduled at
    // minDuration of the initial phase (NOT its full `duration`), offset by the load-time clock
    // (0 here). offset != 0 (a mid-cycle start) is not handled -- see the class scope note.
    public void Reset()
    {
        Step = 0;
        _lastSwitch = _begin;
        _nextSwitchTime = _begin + _logic.Phases[0].MinDuration;
        foreach (var det in _detectors)
        {
            det.Reset();
        }
    }

    // MSActuatedTrafficLightLogic::trySwitch is EVENT-DRIVEN: SUMO schedules it as a begin-of-step
    // event at the phase's computed end and re-schedules it on each call. We replay that here:
    // process every switch event whose time has come by `evalTime`, using the detector state as it
    // stands right now (settled by the previous step's moves -- exactly SUMO's begin-of-step
    // ordering, where trySwitch at time tau sees detector leave-times through step tau-1).
    public void Advance(double evalTime)
    {
        // Guard against a pathological non-advancing schedule (never hit in practice: every path
        // returns a strictly positive delay -- minRetry TIME2STEPS(1) or a minDur).
        var safety = 0;
        while (evalTime >= _nextSwitchTime && safety++ < 1000)
        {
            TrySwitch(_nextSwitchTime);
        }
    }

    // MSActuatedTrafficLightLogic::trySwitch (:709) -- default arm only. `now` is the scheduled
    // switch time (SUMO's getCurrentTimeStep() at the begin-of-step event). Sets Step/_lastSwitch
    // and schedules the next switch (_nextSwitchTime = now + returned delay).
    private void TrySwitch(double now)
    {
        var origStep = Step;
        var actDuration = now - _lastSwitch;

        var detectionGap = GapControl(now);
        double delay;
        if (!double.IsPositiveInfinity(detectionGap))
        {
            // Extend the current (green) phase.
            delay = Duration(now, detectionGap);
            _nextSwitchTime = now + delay;
            return;
        }

        // Advance to the next phase (default: (myStep + 1) % nPhases; no nextPhases override in
        // scope).
        var nextStep = (Step + 1) % _logic.Phases.Count;
        Step = nextStep;
        // myPhases[myStep]->myLastSwitch = now; actDuration = 0 after a real switch.
        _lastSwitch = now;
        actDuration = 0;

        // MSActuatedTrafficLightLogic.cpp:790 `SUMOTime minRetry = myStep != origStep ? 0 :
        // TIME2STEPS(1);` then `return MAX3(minRetry, getMinDur() - actDuration, getEarliest())`.
        // getEarliest() is 0 with no switching rules. A real switch (nextStep != origStep) -> the
        // new phase's full minDur; a self-retry -> at least 1s.
        var minRetry = nextStep != origStep ? 0.0 : 1.0;
        delay = Math.Max(minRetry, Math.Max(_logic.Phases[Step].MinDuration - actDuration, 0.0));
        _nextSwitchTime = now + delay;
    }

    // MSActuatedTrafficLightLogic::gapControl (:834). Returns the minimum induction-loop gap that
    // is still within max-gap (=> extend), or +inf to END the phase: a non-green phase, maxDur
    // reached, or every loop's gap has opened past max-gap.
    private double GapControl(double now)
    {
        var phase = _logic.Phases[Step];
        if (!IsGreenPhase(phase.State))
        {
            return double.PositiveInfinity;
        }

        var actDuration = now - _lastSwitch;
        if (actDuration >= phase.MaxDuration)
        {
            return double.PositiveInfinity;
        }

        var result = double.PositiveInfinity;
        foreach (var loop in _loopsForPhase[Step])
        {
            // isJammed() is always false (no jam-threshold configured).
            var actualGap = loop.GetTimeSinceLastDetection(now);
            if (actualGap < loop.MaxGap)
            {
                result = Math.Min(result, actualGap);
            }
        }

        return result;
    }

    // MSActuatedTrafficLightLogic::duration (:814). Returns the delay (in seconds) until the next
    // trySwitch, integer-rounded so phases always end on a whole second. All arithmetic is done in
    // integer milliseconds (SUMOTime) to reproduce the `% 1000` rounding and integer division
    // EXACTLY -- a wrong gap here flips the ceil/round and shifts the whole timeline.
    private double Duration(double now, double detectionGap)
    {
        const long ts = 1000; // TIME2STEPS(1.0)
        var actDuration = Sec2Steps(now - _lastSwitch);
        var minDur = Sec2Steps(_logic.Phases[Step].MinDuration);
        var maxDur = Sec2Steps(_logic.Phases[Step].MaxDuration);

        // newDuration = getMinDur() - actDuration;
        // newDuration = MAX3(newDuration, TIME2STEPS(myDetectorGap - detectionGap), SUMOTime(1));
        var newDuration = minDur - actDuration;
        var gapSteps = (long)Math.Floor((DefaultDetectorGap - detectionGap) * 1000.0);
        newDuration = Math.Max(newDuration, Math.Max(gapSteps, ts));

        // cut the decimal places to ensure that phases always have integer duration (round the
        // phase END up to the next whole second):
        //   if (newDuration % 1000 != 0) {
        //       totalDur = newDuration + actDuration;
        //       newDuration = (totalDur / 1000 + 1) * 1000 - actDuration;
        //   }
        if (newDuration % 1000 != 0)
        {
            var totalDur = newDuration + actDuration;
            newDuration = (totalDur / 1000 + 1) * 1000 - actDuration;
        }

        // newDuration = MIN3(newDuration, getMaxDur() - actDuration, getLatest()); getLatest()=inf.
        newDuration = Math.Min(newDuration, maxDur - actDuration);
        return newDuration / 1000.0;
    }

    // TIME2STEPS with the same truncation SUMOTime uses for whole-second inputs; +0.5 guards FP
    // dust on values that are already integers in seconds (minDur/maxDur/actDuration are all whole
    // seconds here).
    private static long Sec2Steps(double seconds) => (long)Math.Round(seconds * 1000.0);

    // MSPhaseDefinition::isGreenPhase(): the state contains at least one 'G'/'g'.
    private static bool IsGreenPhase(string state)
    {
        foreach (var c in state)
        {
            if (c is 'G' or 'g')
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Detector feed (called from Engine.ExecuteMoves). `newTime` is the FCD time this move
    // produces (SUMO's SIMTIME during executeMove = the NEW time), which is what MSInductLoop uses
    // to timestamp entry/leave (MSInductLoop.cpp:145/165). The vehicle's within-step motion along
    // `laneHandle` is (oldPos -> newPos) at (oldSpeed -> newSpeed), length `vehLength`.
    // ------------------------------------------------------------------
    public void NotifyMove(
        int laneHandle, int entityIndex,
        double oldPos, double newPos, double oldSpeed, double newSpeed, double vehLength, double newTime)
    {
        foreach (var det in _detectors)
        {
            if (det.LaneHandle == laneHandle)
            {
                det.NotifyMove(entityIndex, oldPos, newPos, oldSpeed, newSpeed, vehLength, newTime);
            }
        }
    }

    // ------------------------------------------------------------------
    // Build: constructs the detectors + phase-loop assignment from the parsed network. Only called
    // for a tlLogic whose Type == "actuated".
    // ------------------------------------------------------------------
    public static ActuatedTrafficLightLogic Build(TlLogic logic, NetworkModel network, double begin)
    {
        var numLinks = logic.Phases.Count > 0 ? logic.Phases[0].State.Length : 0;

        // Map link index -> the incoming lane it leaves (from the tl-controlled connections).
        var laneByLink = new Dictionary<int, Lane>();
        foreach (var conn in network.Connections)
        {
            if (conn.Tl == logic.Id && conn.LinkIndex is { } li)
            {
                var laneId = $"{conn.From}_{conn.FromLane}";
                if (network.LanesById.TryGetValue(laneId, out var lane))
                {
                    laneByLink[li] = lane;
                }
            }
        }

        // getMinimumMinDuration(lane): min over actuated phases where the lane has a green link, of
        // that phase's minDur. Also records which lanes are relevant (built a detector for).
        var minMinDurByLaneHandle = new Dictionary<int, double>();
        foreach (var (li, lane) in laneByLink)
        {
            double best = double.PositiveInfinity;
            foreach (var phase in logic.Phases)
            {
                if (phase.IsActuated && li < phase.State.Length && IsGreen(phase.State[li]))
                {
                    best = Math.Min(best, phase.MinDuration);
                }
            }

            if (!double.IsPositiveInfinity(best))
            {
                if (!minMinDurByLaneHandle.TryGetValue(lane.Handle, out var prev) || best < prev)
                {
                    minMinDurByLaneHandle[lane.Handle] = best;
                }
            }
        }

        // Build one induction loop per relevant lane. ilpos = laneLength - inductLoopPosition,
        // inductLoopPosition = MIN2(detectorGap*speed, (minDur/passingTime + 0.5)*7.5)
        // (MSActuatedTrafficLightLogic.cpp:205-210). detLength 0 => endPos == ilpos.
        var detectorByLaneHandle = new Dictionary<int, Detector>();
        foreach (var (laneHandle, minDur) in minMinDurByLaneHandle)
        {
            var lane = network.LanesByHandle[laneHandle];
            var inductLoopPosition = Math.Min(
                DefaultDetectorGap * lane.Speed,
                (minDur / DefaultPassingTime + 0.5) * DefaultLengthWithGap);
            var ilpos = lane.Length - inductLoopPosition;
            // Upstream-walk for ilpos < 0 (short lanes) is not needed here (single-incoming-lane
            // anchor); clamp to 0 as SUMO does at the end of that walk.
            if (ilpos < 0)
            {
                ilpos = 0;
            }

            detectorByLaneHandle[laneHandle] = new Detector(laneHandle, ilpos, DefaultMaxGap);
        }

        // myInductLoopsForPhase[p]: the detectors of lanes with a green link in phase p, but ONLY
        // for an actuated phase (a non-actuated yellow/all-red phase gets an empty set, so
        // gapControl returns +inf and it ends at its fixed duration).
        var loopsForPhase = new List<IReadOnlyList<Detector>>(logic.Phases.Count);
        foreach (var phase in logic.Phases)
        {
            var loops = new List<Detector>();
            if (phase.IsActuated)
            {
                for (var li = 0; li < phase.State.Length; li++)
                {
                    if (IsGreen(phase.State[li])
                        && laneByLink.TryGetValue(li, out var lane)
                        && detectorByLaneHandle.TryGetValue(lane.Handle, out var det)
                        && !loops.Contains(det))
                    {
                        loops.Add(det);
                    }
                }
            }

            loopsForPhase.Add(loops);
        }

        return new ActuatedTrafficLightLogic(logic, begin, detectorByLaneHandle.Values.ToList(), loopsForPhase);
    }

    private static bool IsGreen(char c) => c is 'G' or 'g';

    // MSInductLoop, scoped to what gapControl reads: LastLeaveTime + current occupancy. Placed at
    // `Position` (== EndPosition, detector-length 0) along its lane.
    private sealed class Detector
    {
        public int LaneHandle { get; }
        private readonly double _position;
        public double MaxGap { get; }

        // MSInductLoop.cpp:74 myLastLeaveTime(-3600).
        private double _lastLeaveTime = -3600.0;
        // myVehiclesOnDet: entityIndex -> entryTime (entryTime itself is unused by gapControl but
        // membership => "occupied" => getTimeSinceLastDetection returns 0).
        private readonly HashSet<int> _onDet = new();

        public Detector(int laneHandle, double position, double maxGap)
        {
            LaneHandle = laneHandle;
            _position = position;
            MaxGap = maxGap;
        }

        public void Reset()
        {
            _lastLeaveTime = -3600.0;
            _onDet.Clear();
        }

        // MSInductLoop::getTimeSinceLastDetection (:274): occupied => 0, else SIMTIME - lastLeave.
        public double GetTimeSinceLastDetection(double now) =>
            _onDet.Count != 0 ? 0.0 : now - _lastLeaveTime;

        // MSInductLoop::notifyMove (:129), Euler arm. Front crossing `_position` => enter; back
        // crossing `_position` (== endPos) => leave, stamping lastLeaveTime = newTime +
        // passingTime(oldBackPos, endPos, newBackPos, ...).
        public void NotifyMove(
            int entityIndex, double oldPos, double newPos, double oldSpeed, double newSpeed, double vehLength, double newTime)
        {
            // entered the detector by move
            if (newPos >= _position && oldPos < _position)
            {
                _onDet.Add(entityIndex);
            }

            var oldBackPos = oldPos - vehLength;
            var newBackPos = newPos - vehLength;
            if (newBackPos > _position && oldBackPos <= _position)
            {
                if (_onDet.Remove(entityIndex))
                {
                    var leaveTime = newTime + PassingTime(oldBackPos, _position, newBackPos, newSpeed);
                    _lastLeaveTime = leaveTime;
                }
            }
        }

        // MSCFModel::passingTime (:658), Euler arm: fraction of the step at which the back reaches
        // `passedPos`, = (passedPos - lastPos)/currentSpeed clamped to [0, TS=1]. currentSpeed 0 =>
        // the whole step (TS).
        private static double PassingTime(double lastPos, double passedPos, double currentPos, double currentSpeed)
        {
            if (currentSpeed == 0.0)
            {
                return 1.0;
            }

            var t = (passedPos - lastPos) / currentSpeed;
            return Math.Min(1.0, Math.Max(0.0, t));
        }
    }
}
