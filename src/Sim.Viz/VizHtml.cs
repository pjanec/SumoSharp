using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sim.Viz;

// ---------------------------------------------------------------------------------------------
// The ONE marker-injection HTML writer, shared by every replay producer (the Sim.Viz demo gallery
// and the IgBridge side-by-side). It serializes the payload (CamelCase, nothing ignored, not
// indented -- the exact options the gallery has always used, so `UseDataHeading -> useDataHeading`)
// into template.html's `/*REPLAY_DATA*/` marker and splices template.js into `/*TEMPLATE_JS*/`.
//
// `payload` is typed `object` on purpose: the gallery passes a `ReplayData` record; VizExport passes
// its own anonymous `{ scenes }` graph (which additionally carries `vehIds` for click-to-identify).
// Serializing `object` lets BOTH route through here without exposing Sim.Viz's internal payload
// records across the assembly boundary -- there is now exactly one copy of the inject glue.
//
// `templateDir` is where template.{html,js} live. It defaults to the directory next to the running
// exe (Sim.Viz.csproj copies the templates there); callers whose exe does not sit beside the
// templates -- e.g. Sim.IgBridge.Host -- pass an explicit dir (the repo's src/Sim.Viz).
// ---------------------------------------------------------------------------------------------
public static class VizHtml
{
    // The single serialization contract for the payload JSON. CamelCase so record/anonymous property
    // names map to the template.js field names (e.g. UseDataHeading -> useDataHeading); Never so no
    // null/default field is dropped; not indented so the embedded blob stays compact.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static bool Write(object payload, string title, string outPath, string? templateDir = null)
    {
        templateDir ??= AppContext.BaseDirectory;
        var templateHtmlPath = Path.Combine(templateDir, "template.html");
        var templateJsPath = Path.Combine(templateDir, "template.js");
        if (!File.Exists(templateHtmlPath) || !File.Exists(templateJsPath))
        {
            Console.Error.WriteLine(
                $"error: template files not found ({templateHtmlPath}, {templateJsPath})");
            return false;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var html = File.ReadAllText(templateHtmlPath)
            .Replace("__SCENARIO_NAME__", title)
            .Replace("/*REPLAY_DATA*/", json)
            .Replace("/*TEMPLATE_JS*/", File.ReadAllText(templateJsPath));

        File.WriteAllText(outPath, html);
        return true;
    }
}
