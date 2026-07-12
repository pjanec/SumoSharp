namespace Sim.Core;

// SUMOSHARP-API.md §4.3-4.4 (D5): the replacement for the old `Dictionary<string, ExternalObstacle>`
// external-obstacle store. A DIRECT-MAPPED struct-of-arrays: an ObstacleHandle's Index is the literal
// slot into every column array (matching the host engine's "Index points directly to the slot"
// convention), a per-slot Generation validates the handle in O(1), and a dense `_active` index list
// gives the engine's per-step scans cache-friendly iteration over only the live slots.
//
// Why this shape:
//   - UpdateObstacle(handle, ...) is a generation check + a direct column write: zero allocation, no
//     hash, no indirection -- the per-step correction path a crowd sim hammers with thousands of agents.
//   - A stale/removed handle fails the generation check -> inert no-op (the "inert-when-absent" contract).
//   - Iteration order is INSERTION order (append-on-add), disturbed only by swap-remove on Remove.
//     Every engine consumer of the store is an order-independent min/threat reduction or set-membership
//     test (ObstacleConstraint, ExternalAgentOnFoeLane, the follower/junction scans), except the reroute
//     blocked-edge scan which breaks on the first match; all are byte-identical for the <=1-matching-
//     obstacle case every committed scenario uses, and the parity suite is the gate.
//   - The lateral columns (LatPos/Width/LatSpeed) and the reserved AvoidanceClass byte are exactly what
//     the laneless/RVO layer's neutral RvoNeighbor adapter reads (LANELESS-DIRECTION.md §15).
//
// Not thread-safe: mutated only from the single-threaded Input phase (AddObstacle/Update/Advance) and
// read during the parallel-safe plan phase after all mutations for the step are done -- the same
// start-of-step-frozen contract `_obstacles` always had (CLAUDE.md rule 2).
internal sealed class ObstacleStore
{
    private const int InitialCapacity = 8;

    // Columns, indexed directly by slot (== ObstacleHandle.Index). Parallel arrays, grown together.
    private string[] _id = new string[InitialCapacity];
    private string[] _laneId = new string[InitialCapacity];
    private int[] _laneHandle = new int[InitialCapacity];
    private double[] _frontPos = new double[InitialCapacity];
    private double[] _length = new double[InitialCapacity];
    private double[] _startTime = new double[InitialCapacity];
    private double[] _endTime = new double[InitialCapacity];
    private double[] _speed = new double[InitialCapacity];
    private double[] _maxDecel = new double[InitialCapacity];
    private double[] _latPos = new double[InitialCapacity];
    private double[] _width = new double[InitialCapacity];
    private double[] _latSpeed = new double[InitialCapacity];
    private byte[] _avoidanceClass = new byte[InitialCapacity];

    // Per-slot generation (starts at 1 for a never-used slot so a `default` handle {0,0} never
    // resolves) and liveness flag. `_activePos[slot]` is the slot's position in `_active`, or -1.
    private ushort[] _generation = new ushort[InitialCapacity];
    private bool[] _alive = new bool[InitialCapacity];
    private int[] _activePos = new int[InitialCapacity];

    // Dense list of live slot indices (iteration order); free list of recycled slots.
    private readonly List<int> _active = new();
    private readonly Stack<int> _free = new();

    // Next never-yet-allocated slot (when the free list is empty).
    private int _highWater;

    public ObstacleStore()
    {
        for (var i = 0; i < InitialCapacity; i++)
        {
            _generation[i] = 1;
            _activePos[i] = -1;
        }
    }

    // Number of LIVE obstacles (added, not removed). Mirrors the old `_obstacles.Count` the engine's
    // `.Count == 0` inert-when-absent fast paths test.
    public int Count => _active.Count;

    public ObstacleHandle Add(
        string id, int laneHandle, string laneId,
        double frontPos, double length, double startTime, double endTime,
        double speed, double maxDecel, double latPos, double width, double latSpeed,
        AvoidanceClass avoidanceClass)
    {
        int slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
        }
        else
        {
            slot = _highWater++;
            EnsureCapacity(slot + 1);
        }

        _id[slot] = id;
        _laneId[slot] = laneId;
        _laneHandle[slot] = laneHandle;
        _frontPos[slot] = frontPos;
        _length[slot] = length;
        _startTime[slot] = startTime;
        _endTime[slot] = endTime;
        _speed[slot] = speed;
        _maxDecel[slot] = maxDecel;
        _latPos[slot] = latPos;
        _width[slot] = width;
        _latSpeed[slot] = latSpeed;
        _avoidanceClass[slot] = (byte)avoidanceClass;

        _alive[slot] = true;
        _activePos[slot] = _active.Count;
        _active.Add(slot);

        return new ObstacleHandle((uint)slot, _generation[slot]);
    }

    // Resolve a handle to its live slot; false (with slot = -1) if stale/removed/out-of-range.
    private bool TryResolve(ObstacleHandle h, out int slot)
    {
        slot = (int)h.Index;
        if (slot < 0 || slot >= _highWater || !_alive[slot] || _generation[slot] != h.Generation)
        {
            slot = -1;
            return false;
        }

        return true;
    }

    public bool IsAlive(ObstacleHandle h) => TryResolve(h, out _);

    // Per-step corrections from the external owner (SUMOSHARP-API.md §4.4). Inert no-op on a stale
    // handle. Overloads mirror the old string-keyed UpdateObstacle set (long-only / +latPos / +latSpeed).
    public bool Update(ObstacleHandle h, double frontPos, double speed)
    {
        if (!TryResolve(h, out var slot))
        {
            return false;
        }

        _frontPos[slot] = frontPos;
        _speed[slot] = speed;
        return true;
    }

    public bool Update(ObstacleHandle h, double frontPos, double speed, double latPos)
    {
        if (!TryResolve(h, out var slot))
        {
            return false;
        }

        _frontPos[slot] = frontPos;
        _speed[slot] = speed;
        _latPos[slot] = latPos;
        return true;
    }

    public bool Update(ObstacleHandle h, double frontPos, double speed, double latPos, double latSpeed)
    {
        if (!TryResolve(h, out var slot))
        {
            return false;
        }

        _frontPos[slot] = frontPos;
        _speed[slot] = speed;
        _latPos[slot] = latPos;
        _latSpeed[slot] = latSpeed;
        return true;
    }

    public bool Remove(ObstacleHandle h)
    {
        if (!TryResolve(h, out var slot))
        {
            return false;
        }

        RemoveSlot(slot);
        return true;
    }

    public void Clear()
    {
        // Bump every live slot's generation and recycle it, so any handle held by a caller across a
        // Clear becomes stale (never silently addresses a future obstacle at the same index).
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var slot = _active[i];
            _alive[slot] = false;
            _activePos[slot] = -1;
            _generation[slot]++;
            _free.Push(slot);
        }

        _active.Clear();
    }

    private void RemoveSlot(int slot)
    {
        // Swap-remove from the dense active list so it stays packed and Remove is O(1).
        var pos = _activePos[slot];
        var lastPos = _active.Count - 1;
        var lastSlot = _active[lastPos];
        _active[pos] = lastSlot;
        _activePos[lastSlot] = pos;
        _active.RemoveAt(lastPos);

        _activePos[slot] = -1;
        _alive[slot] = false;
        _generation[slot]++;  // invalidate stale handles to this slot
        _id[slot] = null!;    // drop the string reference (GC), never read while !_alive
        _laneId[slot] = null!;
        _free.Push(slot);
    }

    // B5-i dead-reckoning, moved verbatim from Engine.AdvanceObstacles: extrapolate every MOVING
    // obstacle's FrontPos (and B6-lat LatPos) by its reported velocity, once per step, in the Input
    // phase -- so the plan phase reads a frozen, already-advanced position. Iterates the dense active
    // list and writes columns in place (no id-scratch snapshot / no enumerate-during-mutation hazard the
    // dictionary version had). A Speed==0 && LatSpeed==0 obstacle, or one outside [StartTime, EndTime)
    // at `time`, or one at/before its StartTime, is skipped -- byte-identical to the pre-store logic.
    public void Advance(double time, double dt)
    {
        for (var i = 0; i < _active.Count; i++)
        {
            var slot = _active[i];
            var speed = _speed[slot];
            var latSpeed = _latSpeed[slot];
            if ((speed == 0.0 && latSpeed == 0.0)
                || _startTime[slot] >= time || time >= _endTime[slot])
            {
                continue;
            }

            _frontPos[slot] += speed * dt;
            _latPos[slot] += latSpeed * dt;
        }
    }

    private void EnsureCapacity(int needed)
    {
        var cap = _generation.Length;
        if (needed <= cap)
        {
            return;
        }

        var newCap = cap;
        while (newCap < needed)
        {
            newCap *= 2;
        }

        Array.Resize(ref _id, newCap);
        Array.Resize(ref _laneId, newCap);
        Array.Resize(ref _laneHandle, newCap);
        Array.Resize(ref _frontPos, newCap);
        Array.Resize(ref _length, newCap);
        Array.Resize(ref _startTime, newCap);
        Array.Resize(ref _endTime, newCap);
        Array.Resize(ref _speed, newCap);
        Array.Resize(ref _maxDecel, newCap);
        Array.Resize(ref _latPos, newCap);
        Array.Resize(ref _width, newCap);
        Array.Resize(ref _latSpeed, newCap);
        Array.Resize(ref _avoidanceClass, newCap);
        Array.Resize(ref _generation, newCap);
        Array.Resize(ref _alive, newCap);
        Array.Resize(ref _activePos, newCap);

        for (var i = cap; i < newCap; i++)
        {
            _generation[i] = 1;
            _activePos[i] = -1;
        }
    }

    // Zero-allocation read view: `foreach (var o in store.Values)` materialises each live slot's columns
    // into an ExternalObstacle VALUE (a stack copy), so every existing `foreach (var o in
    // _obstacles.Values)` consumer in Engine compiles and behaves unchanged. Struct enumerator (no heap
    // allocation for the iteration itself), walking the dense active list.
    public ValuesView Values => new(this);

    public readonly struct ValuesView
    {
        private readonly ObstacleStore _store;

        public ValuesView(ObstacleStore store) => _store = store;

        public Enumerator GetEnumerator() => new(_store);

        public struct Enumerator
        {
            private readonly ObstacleStore _store;
            private int _i;

            public Enumerator(ObstacleStore store)
            {
                _store = store;
                _i = -1;
            }

            public readonly ExternalObstacle Current
            {
                get
                {
                    var slot = _store._active[_i];
                    return new ExternalObstacle(
                        _store._id[slot],
                        _store._laneId[slot],
                        _store._frontPos[slot],
                        _store._length[slot],
                        _store._startTime[slot],
                        _store._endTime[slot],
                        _store._speed[slot],
                        _store._maxDecel[slot],
                        _store._latPos[slot],
                        _store._width[slot],
                        _store._latSpeed[slot],
                        (AvoidanceClass)_store._avoidanceClass[slot]);
                }
            }

            public bool MoveNext() => ++_i < _store._active.Count;
        }
    }
}
