using System.Numerics;
using ImGuiNET;

namespace Sim.Viewer;

// docs/SUMOSHARP-PACKAGING-DESIGN.md D5/D10 (P3.2): the demo tool's ImGui "Demos" picker -- always
// present, GENERIC (categories + selectables + current highlight only). This used to live in
// Renderer.cs (the generic renderer) and also drew the evac legend/counters inline; that domain content
// now lives in EvacOverlay.DrawUi, called separately by Program.cs's RunLocal alongside this one. Moving
// this here keeps Renderer.cs -- and by extension a future packaged generic viewer -- free of demo-tool
// UI, matching DemoCatalog/DemoSession's move out of Sim.Viewer.Core.
public static class DemoUi
{
    // docs/SUMOSHARP-VIEWER-DEMO-EVAC-DESIGN.md §6 / -TASKS.md T5: groups `resolved` (DemoCatalog.
    // Resolve's output) into a collapsing header per DemoCategory that has at least one entry; each entry
    // is a Selectable row (blurb on hover) that queues a switch via `session.RequestSwitch` -- the actual
    // dispose-old/build-new work happens at the top of the next frame via DemoSession.TryApplyPending
    // (wired in RunLocal), so this method never touches EngineHost lifetime itself.
    public static void DrawDemosPanel(DemoSession session, IReadOnlyList<DemoEntry> resolved)
    {
        ImGui.SetNextWindowPos(new Vector2(900, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(370, 760), ImGuiCond.FirstUseEver);
        ImGui.Begin("SumoSharp - demos");

        var current = session.Current;
        ImGui.Text(current is not null ? $"current: {current.Name}" : "current: (custom)");
        ImGui.Separator();

        foreach (var category in Enum.GetValues<DemoCategory>())
        {
            var hasEntries = false;
            foreach (var e in resolved)
            {
                if (e.Category == category)
                {
                    hasEntries = true;
                    break;
                }
            }

            if (!hasEntries)
            {
                continue;
            }

            if (ImGui.CollapsingHeader(category.ToString(), ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var entry in resolved)
                {
                    if (entry.Category != category)
                    {
                        continue;
                    }

                    var isCurrent = current is not null && ReferenceEquals(entry, current);
                    if (ImGui.Selectable(entry.Name, isCurrent))
                    {
                        session.RequestSwitch(entry);
                    }

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(entry.Blurb);
                    }
                }
            }
        }

        ImGui.End();
    }
}
