using System.Numerics;
using rlImGui_cs;
using ImGuiNET;

namespace Sim.Viewer.Raylib;

// `using Raylib_cs;` at namespace-body level (not compilation-unit level) to dodge the
// `Sim.Viewer.Raylib` vs `Raylib_cs.Raylib` simple-name collision -- see the identical note in Renderer.cs.
using Raylib_cs;

// docs/SUMOSHARP-PACKAGING-DESIGN.md D5/§5/§6 (Option A follow-up): the REUSABLE raylib render loop,
// extracted from the Sim.Viewer demo exe's per-mode Run* methods into the package so any consumer can drive
// a SUMO stream through the same window shell instead of re-writing it. ViewerHost owns everything the three
// modes (local authoritative / loopback DR / remote DR) had byte-for-byte in common: window + font
// bootstrap, the 60 fps loop, the Camera2D pan/zoom/click state machine, the 'D' diagnostics toggle, window
// resize, the fixed BeginDrawing -> world -> ImGui -> EndDrawing order, the headless screenshot/frames exit,
// and shutdown. Everything that actually DIFFERED between modes is supplied by the caller through
// ViewerHostConfig callbacks: the frame source (PumpFrame), the world draw (DrawWorld -- genuinely different:
// local bakes a RoadLayerCache + overlay under/over around DrawDynamicWorld, DDS modes call the single
// DrawWorldDds), the ImGui panels (DrawImGui), the click action (OnWorldClick), and the camera-bounds
// sources (initial / demo-switch refit / late-geometry refit). The demo's Run* methods become: build the
// mode's host/subscriber, wire a config, call ViewerHost.Run.
public sealed class ViewerHostConfig
{
    public string WindowTitle = "SumoSharp - native viewer";
    public int Width = 1280;
    public int Height = 800;
    public int TargetFps = 60;
    public bool ResizableWindow = true;

    // Non-null => HEADLESS: after `Frames` rendered frames, export a screenshot to this path and exit.
    public string? ScreenshotPath;
    public int Frames;

    // 'D'-toggled diagnostics flag's initial value (passed to DrawImGui each frame).
    public bool ShowDiagnosticsInitially = true;

    // Pre-size the reused vehicle-draw list to the expected fleet so it doesn't grow/realloc during warmup
    // (the local mode's documented anti-stutter sizing).
    public int DrawCapacity = 256;

    // Camera fit on startup (world bounds MinX/MinY/MaxX/MaxY). Null => a small placeholder view (remote,
    // before any geometry has arrived). Read once, before the loop.
    public Func<(double MinX, double MinY, double MaxX, double MaxY)?> InitialCameraBounds = () => null;

    // Optional: runs at the TOP of each frame, BEFORE input (local's demo hot-swap). If it returns bounds,
    // ViewerHost re-fits the camera to them (a demo switch changed the net); the callback itself handles any
    // other per-switch reset (road-layer invalidate, render-state clear). Return null to leave the camera.
    public Func<(double MinX, double MinY, double MaxX, double MaxY)?>? OnFrameStart;

    // Optional: a camera refit that engages once some condition first becomes true (remote latches onto the
    // first received geometry). Runs after PumpFrame, before drawing. Return bounds to refit; null to leave.
    public Func<(double MinX, double MinY, double MaxX, double MaxY)?>? RefitCameraBounds;

    // Optional: window resized. ViewerHost has already updated the camera offset; the mode syncs its own
    // size-dependent resources (local: the RoadLayerCache RenderTexture). (w, h) = new framebuffer size.
    public Action<int, int>? OnResize;

    // Advance the frame source and fill `draws` (reused; already Clear-able by the callback as needed).
    // `renderDt` = Raylib.GetFrameTime(). Runs before drawing and reads no camera input.
    public Action<float, List<Renderer.DrVehicleDraw>> PumpFrame = (_, __) => { };

    // A world CLICK that was not a pan-drag. (worldX, worldY) are FLIP-space with Y ALREADY NEGATED (i.e.
    // SUMO world coords), so the callback can pass them straight to host.InjectObstacleAtWorld /
    // IRenderOverlay.OnWorldClick / a command channel. Null => clicks do nothing.
    public Action<double, double>? OnWorldClick;

    // Draw the mode's WORLD (static + dynamic + overlay). Runs inside BeginDrawing, after ClearBackground and
    // the input block, inside rlImGui.Begin/End. Local: road-layer static + overlay under / DrawDynamicWorld
    // / overlay over. DDS: Renderer.DrawWorldDds (+ any waiting banner).
    public Action<Camera2D, List<Renderer.DrVehicleDraw>> DrawWorld = (_, __) => { };

    // Draw the mode's ImGui panels. `showDiagnostics` is the ViewerHost-owned, 'D'-toggled flag; the mode
    // decides what to gate on it. Runs after DrawWorld, still inside rlImGui.Begin/End.
    public Action<bool> DrawImGui = _ => { };

    // Optional: end-of-frame side effects (local's --perf per-second log). Return true to STOP the loop
    // (local's --seconds cap). Runs after EndDrawing and after the headless screenshot check.
    public Func<bool>? OnFrameEnd;

    // Optional: extra logging right after the headless screenshot, before the loop breaks (the DDS modes'
    // DRCLOCK line).
    public Action? OnHeadlessExit;
}

public static class ViewerHost
{
    private const float DragMoveThreshold = 3f; // px: below this, a mouseup is a CLICK (pick), not a pan.

    public static int Run(ViewerHostConfig cfg)
    {
        var screenW = cfg.Width;
        var screenH = cfg.Height;

        global::Raylib_cs.Raylib.SetTraceLogLevel(TraceLogLevel.Warning);
        if (cfg.ResizableWindow)
        {
            global::Raylib_cs.Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        }

        global::Raylib_cs.Raylib.InitWindow(screenW, screenH, cfg.WindowTitle);
        if (!global::Raylib_cs.Raylib.IsWindowReady())
        {
            Console.Error.WriteLine("Sim.Viewer: window not ready (no display?).");
            return 1;
        }

        global::Raylib_cs.Raylib.SetTargetFPS(cfg.TargetFps);

        rlImGui.Setup(darkTheme: true, enableDocking: false);
        var io = ImGui.GetIO();
        io.Fonts.Clear();
        var fontPath = Path.Combine(AppContext.BaseDirectory, "assets", "DejaVuSans.ttf");
        io.Fonts.AddFontFromFileTTF(fontPath, 18f);
        rlImGui.ReloadFonts();

        var camera = FitFromBounds(cfg.InitialCameraBounds(), screenW, screenH)
                     ?? Renderer.FitCamera(-50, -50, 50, 50, screenW, screenH);

        var headless = cfg.ScreenshotPath is not null;
        var frameCount = 0;
        var frameStats = new FrameStats();
        var showDiagnostics = cfg.ShowDiagnosticsInitially;

        var dragging = false;
        var dragMoved = false;
        var dragStartMouse = Vector2.Zero;
        var dragStartTarget = Vector2.Zero;

        var draws = new List<Renderer.DrVehicleDraw>(cfg.DrawCapacity);

        while (!global::Raylib_cs.Raylib.WindowShouldClose())
        {
            frameStats.Add(global::Raylib_cs.Raylib.GetFrameTime());

            // Top-of-frame hook, before anything this frame reads the source (local's queued demo switch:
            // swap host + overlay, invalidate the road cache, reset render state -- all inside the callback --
            // and return the new net's bounds so ViewerHost re-fits the camera).
            if (cfg.OnFrameStart is not null && cfg.OnFrameStart() is { } sb)
            {
                camera = Renderer.FitCamera(sb.MinX, sb.MinY, sb.MaxX, sb.MaxY, screenW, screenH);
            }

            if (global::Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.D))
            {
                showDiagnostics = !showDiagnostics;
            }

            // Window resized: re-centre the camera offset so the current view stays put, and let the mode
            // match any size-dependent resource to the new framebuffer. Guard the degenerate/minimized 0-size
            // case (LoadRenderTexture(0,0) segfaults the GL driver).
            if (global::Raylib_cs.Raylib.IsWindowResized() && !global::Raylib_cs.Raylib.IsWindowMinimized())
            {
                var w = global::Raylib_cs.Raylib.GetScreenWidth();
                var h = global::Raylib_cs.Raylib.GetScreenHeight();
                if (w > 0 && h > 0)
                {
                    screenW = w;
                    screenH = h;
                    camera.Offset = new Vector2(w / 2f, h / 2f);
                    cfg.OnResize?.Invoke(w, h);
                }
            }

            // Advance the frame source (pump DDS / read the authoritative snapshot pair) and build this
            // frame's draw poses. Independent of camera input, so it runs before the input/draw block.
            cfg.PumpFrame(global::Raylib_cs.Raylib.GetFrameTime(), draws);

            // Late-geometry / conditional refit (remote: fit once the first geometry arrives, then latch).
            if (cfg.RefitCameraBounds is not null && cfg.RefitCameraBounds() is { } rb)
            {
                camera = Renderer.FitCamera(rb.MinX, rb.MinY, rb.MaxX, rb.MaxY, screenW, screenH);
            }

            global::Raylib_cs.Raylib.BeginDrawing();
            global::Raylib_cs.Raylib.ClearBackground(Renderer.BackgroundColor);

            // rlImGui.Begin() (ImGui NewFrame) must run before reading WantCaptureMouse for this frame's
            // world-input gate: an ImGui window/button under the cursor should eat the click/drag/wheel rather
            // than also panning/picking the world beneath it.
            rlImGui.Begin();
            var wantMouse = ImGui.GetIO().WantCaptureMouse;

            if (!wantMouse)
            {
                var mouse = global::Raylib_cs.Raylib.GetMousePosition();

                if (global::Raylib_cs.Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    dragging = true;
                    dragMoved = false;
                    dragStartMouse = mouse;
                    dragStartTarget = camera.Target;
                }

                if (dragging && global::Raylib_cs.Raylib.IsMouseButtonDown(MouseButton.Left))
                {
                    var delta = mouse - dragStartMouse;
                    if (delta.Length() > DragMoveThreshold)
                    {
                        dragMoved = true;
                    }

                    // Pan: drag the WORLD with the cursor -> Target moves opposite the screen-space delta,
                    // scaled back to world units.
                    camera.Target = dragStartTarget - delta / camera.Zoom;
                }

                if (global::Raylib_cs.Raylib.IsMouseButtonReleased(MouseButton.Left))
                {
                    if (dragging && !dragMoved && cfg.OnWorldClick is not null)
                    {
                        // A click, not a pan -> the WORLD point under the cursor (FLIP-space; negate Y to
                        // hand back SUMO world coords).
                        var flip = global::Raylib_cs.Raylib.GetScreenToWorld2D(mouse, camera);
                        cfg.OnWorldClick(flip.X, -flip.Y);
                    }

                    dragging = false;
                }

                var wheel = global::Raylib_cs.Raylib.GetMouseWheelMove();
                if (wheel != 0f)
                {
                    // Zoom about the cursor: the world point under the cursor must land back under it after
                    // the zoom, so re-derive Target from the pre-zoom world point.
                    var beforeZoom = global::Raylib_cs.Raylib.GetScreenToWorld2D(mouse, camera);
                    var zoomFactor = wheel > 0 ? 1.1f : 1f / 1.1f;
                    camera.Zoom *= zoomFactor;
                    var afterZoom = global::Raylib_cs.Raylib.GetScreenToWorld2D(mouse, camera);
                    camera.Target += beforeZoom - afterZoom;
                }
            }

            cfg.DrawWorld(camera, draws);
            cfg.DrawImGui(showDiagnostics);

            rlImGui.End();
            global::Raylib_cs.Raylib.EndDrawing();

            frameCount++;
            if (headless && frameCount >= cfg.Frames)
            {
                ExportScreenshot(cfg.ScreenshotPath!);
                cfg.OnHeadlessExit?.Invoke();
                break;
            }

            if (cfg.OnFrameEnd is not null && cfg.OnFrameEnd())
            {
                break;
            }
        }

        rlImGui.Shutdown();
        global::Raylib_cs.Raylib.CloseWindow();
        return 0;
    }

    private static Camera2D? FitFromBounds((double MinX, double MinY, double MaxX, double MaxY)? bounds, int w, int h)
        => bounds is { } b ? Renderer.FitCamera(b.MinX, b.MinY, b.MaxX, b.MaxY, w, h) : null;

    // NOT Raylib.TakeScreenshot(path): raylib's TakeScreenshot silently drops the directory portion of `path`
    // (it saves GetFileName(path) under its internal base path, i.e. the process's working directory at
    // InitWindow time). LoadImageFromScreen + ExportImage writes to the exact path given, honoring an absolute
    // --screenshot path as the CLI promises.
    private static void ExportScreenshot(string screenshotPath)
    {
        var absolutePath = Path.GetFullPath(screenshotPath);
        var screenImage = global::Raylib_cs.Raylib.LoadImageFromScreen();
        global::Raylib_cs.Raylib.ExportImage(screenImage, absolutePath);
        global::Raylib_cs.Raylib.UnloadImage(screenImage);
    }
}
