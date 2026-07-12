namespace Sim.Core;

// SUMOSHARP-API.md §9: a lightweight handle to a registered vehicle type. Index into the engine's vType
// id table (both loaded and runtime-defined types get one), so a host addresses a vType without the
// string id in the hot path. `None`/`IsValid` guard unresolved lookups.
public readonly struct VTypeHandle : IEquatable<VTypeHandle>
{
    public readonly int Index;

    public VTypeHandle(int index) => Index = index;

    public static VTypeHandle None => new(-1);

    public bool IsValid => Index >= 0;

    public bool Equals(VTypeHandle other) => Index == other.Index;

    public override bool Equals(object? obj) => obj is VTypeHandle h && Equals(h);

    public override int GetHashCode() => Index;

    public static bool operator ==(VTypeHandle a, VTypeHandle b) => a.Index == b.Index;

    public static bool operator !=(VTypeHandle a, VTypeHandle b) => a.Index != b.Index;

    public override string ToString() => $"VType#{Index}";
}
