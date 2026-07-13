using Sim.Evac;
using Xunit;

namespace Sim.ParityTests;

// PANIC-EVAC.md R3 / §8.1 unit coverage for Sim.Evac.Incident: the pure radius-only fear function
// and its activation gate. No Engine/network involved -- Incident is a standalone value type.
public class IncidentTests
{
    private static readonly Incident TheIncident = new(X: 100.0, Y: 50.0, StartTime: 10.0, Radius: 40.0);

    [Fact]
    public void IsActive_FalseBeforeStart_TrueAtAndAfterStart()
    {
        Assert.False(TheIncident.IsActive(9.999));
        Assert.True(TheIncident.IsActive(10.0));
        Assert.True(TheIncident.IsActive(10.001));
        Assert.True(TheIncident.IsActive(1000.0));
    }

    [Fact]
    public void FearAt_Epicentre_IsOne()
    {
        Assert.Equal(1.0, TheIncident.FearAt(TheIncident.X, TheIncident.Y, TheIncident.StartTime));
        Assert.Equal(1.0, TheIncident.FearAt(TheIncident.X, TheIncident.Y, 500.0));
    }

    [Fact]
    public void FearAt_HalfRadius_IsAboutHalf()
    {
        var x = TheIncident.X + TheIncident.Radius / 2.0;
        var y = TheIncident.Y;
        Assert.Equal(0.5, TheIncident.FearAt(x, y, TheIncident.StartTime), 3);
    }

    [Fact]
    public void FearAt_AtOrBeyondRadius_IsZero()
    {
        var atRadius = TheIncident.X + TheIncident.Radius;
        Assert.Equal(0.0, TheIncident.FearAt(atRadius, TheIncident.Y, TheIncident.StartTime));

        var beyondRadius = TheIncident.X + TheIncident.Radius * 2.0;
        Assert.Equal(0.0, TheIncident.FearAt(beyondRadius, TheIncident.Y, TheIncident.StartTime));
    }

    [Fact]
    public void FearAt_BeforeStartTime_IsZeroEvenAtEpicentre()
    {
        Assert.Equal(0.0, TheIncident.FearAt(TheIncident.X, TheIncident.Y, TheIncident.StartTime - 0.001));
        Assert.Equal(0.0, TheIncident.FearAt(TheIncident.X, TheIncident.Y, 0.0));
    }
}
