using Sim.Core;

namespace Sim.Replication;

// SUMOSHARP-DEADRECKONING.md §7 — the transport-agnostic scheduling loop that turns a per-vehicle
// IPublishPolicy (a stateless predicate) into a per-step "which movers do I send this step?" decision.
// It owns the only state the policy needs but does not keep: each mover's last-published sim time. A
// publisher (DDS / TCP / the LiveHost demo) drives it once per sim step -- ask ShouldPublish per candidate,
// then EndStep to forget despawned movers.
//
// Keyed by VehicleHandle (NO strings -- matches the wire packet). Stateful and single-threaded: this is the
// publish side, which already runs on one thread reading the async snapshot. The policy stays swappable
// (camera-distance weighting, bandwidth governor, ...); the scheduler is the reusable bookkeeping around it,
// previously duplicated in the demo host.
public sealed class PublishScheduler
{
    private readonly IPublishPolicy _policy;
    private readonly Dictionary<VehicleHandle, double> _lastSent = new();
    private readonly HashSet<VehicleHandle> _seen = new();
    private List<VehicleHandle>? _pruneScratch;

    public PublishScheduler(IPublishPolicy policy)
    {
        if (policy is null)
        {
            throw new ArgumentNullException(nameof(policy));
        }

        _policy = policy;
    }

    // Movers currently remembered (i.e. published at least once and not yet pruned). Test/telemetry hook.
    public int TrackedCount => _lastSent.Count;

    // Decide for ONE candidate mover at sim time `time`. Marks it seen (so EndStep won't prune it), then asks
    // the policy using the seconds since this mover was last published (+inf on first sighting -> always
    // sent). Returns true to publish now; when true, records `time` as its new last-sent. `handle` is the
    // mover; the rest are the cheap per-step signals the policy reads.
    public bool ShouldPublish(
        VehicleHandle handle, DrModel model, double speed, double accel, double time, bool laneChangingOrManoeuvring)
    {
        _seen.Add(handle);
        var since = _lastSent.TryGetValue(handle, out var last) ? time - last : double.PositiveInfinity;
        var signals = new PublishSignals(handle, model, speed, accel, since, laneChangingOrManoeuvring);
        if (!_policy.ShouldPublish(signals))
        {
            return false;
        }

        _lastSent[handle] = time;
        return true;
    }

    // Call once after a step's candidates have all been offered to ShouldPublish. Forgets any tracked mover
    // not seen this step (it despawned), keeping memory O(live movers), then resets the per-step seen set.
    public void EndStep()
    {
        if (_lastSent.Count > _seen.Count)
        {
            _pruneScratch ??= new List<VehicleHandle>();
            _pruneScratch.Clear();
            foreach (var kv in _lastSent)
            {
                if (!_seen.Contains(kv.Key))
                {
                    _pruneScratch.Add(kv.Key);
                }
            }

            foreach (var stale in _pruneScratch)
            {
                _lastSent.Remove(stale);
            }
        }

        _seen.Clear();
    }

    // Drop all bookkeeping (e.g. on a scenario reload). The policy is retained.
    public void Reset()
    {
        _lastSent.Clear();
        _seen.Clear();
    }
}
