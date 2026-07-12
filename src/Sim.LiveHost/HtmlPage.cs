namespace Sim.LiveHost;

// The self-contained front-end served at "/". A canvas renderer driven by the WebSocket: it receives the
// network geometry once, then per-frame vehicle state, and on click converts the pixel to WORLD
// coordinates (the inverse camera transform) and sends an obstacle request back. Kept inline so the demo
// is a single runnable project with no static-file plumbing.
internal static class HtmlPage
{
    public const string Html = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SumoSharp — live</title>
<style>
  html,body{margin:0;height:100%;background:#0e1116;color:#e6edf3;font:13px/1.4 system-ui,sans-serif;overflow:hidden}
  #hud{position:fixed;top:10px;left:12px;z-index:10;background:rgba(20,24,31,.82);padding:10px 12px;border-radius:8px;border:1px solid #2b323c}
  #hud b{color:#7ee787}
  #hud .row{margin-top:6px;color:#9da7b3}
  button{background:#21262d;color:#e6edf3;border:1px solid #3b424c;border-radius:6px;padding:5px 9px;cursor:pointer;margin-top:8px}
  button:hover{background:#2d333b}
  canvas{display:block;cursor:crosshair}
</style>
</head>
<body>
<div id="hud">
  <div><b>SumoSharp</b> live &middot; <span id="stat">connecting…</span></div>
  <div class="row"><b>click</b> the road to drop an obstacle</div>
  <div class="row">wheel = zoom &middot; drag = pan</div>
  <button id="clear">clear obstacles</button>
</div>
<canvas id="c"></canvas>
<script>
(function(){
  const cv = document.getElementById('c'), ctx = cv.getContext('2d');
  const stat = document.getElementById('stat');
  let net = null;
  const cam = { scale: 1, ox: 0, oy: 0 };

  // Client-side entity interpolation (the browser analog of SimulationRunner's two-frame interpolation
  // hook, SUMOSHARP-API.md §7). Frames arrive at ~20 fps; requestAnimationFrame draws at ~60 fps. We
  // buffer recent frames and, each rAF, render at a fixed delay in the past (RENDER_DELAY_MS), lerping
  // each vehicle by id between the two buffered frames that bracket that render time -> smooth motion
  // instead of stepping at the arrival rate. The delay is the standard latency-for-smoothness trade.
  const RENDER_DELAY_MS = 110;
  const buf = [];        // recent frames, oldest..newest, each stamped with _recv = arrival time
  const BUF_MAX = 16;

  function pushFrame(m){
    m._recv = performance.now();
    buf.push(m);
    if(buf.length > BUF_MAX) buf.shift();
  }

  function lerp(a, b, t){ return a + (b - a) * t; }

  // The vehicle set to draw at wall-time `renderT`: interpolated between the two bracketing frames, matched
  // by id. Vehicles present in only one side are drawn at their known position (just appeared / leaving).
  function sampleVehicles(renderT){
    if(buf.length === 0) return { vehicles: [], obstacles: [], time: 0 };
    const newest = buf[buf.length - 1];
    if(buf.length === 1 || renderT >= newest._recv){
      return { vehicles: newest.vehicles, obstacles: newest.obstacles, time: newest.time };
    }

    // find the pair (lo, hi) with lo._recv <= renderT <= hi._recv (else clamp to the oldest pair)
    let lo = buf[0], hi = buf[1];
    for(let i = 0; i < buf.length - 1; i++){
      if(buf[i]._recv <= renderT && renderT <= buf[i+1]._recv){ lo = buf[i]; hi = buf[i+1]; break; }
    }
    const span = hi._recv - lo._recv;
    const a = span > 0 ? Math.max(0, Math.min(1, (renderT - lo._recv) / span)) : 1;

    const prevById = new Map();
    for(const v of lo.vehicles) prevById.set(v.id, v);

    const out = [];
    for(const v of hi.vehicles){
      const p = prevById.get(v.id);
      if(p){ out.push({ x: lerp(p.x, v.x, a), y: lerp(p.y, v.y, a), s: lerp(p.s, v.s, a) }); }
      else { out.push({ x: v.x, y: v.y, s: v.s }); }
    }
    return { vehicles: out, obstacles: hi.obstacles, time: hi.time };
  }

  function resize(){ cv.width = innerWidth; cv.height = innerHeight; if(net) draw(); }
  addEventListener('resize', resize);

  function w2s(x,y){ return [x*cam.scale + cam.ox, -y*cam.scale + cam.oy]; }
  function s2w(x,y){ return [(x-cam.ox)/cam.scale, -(y-cam.oy)/cam.scale]; }

  function fit(b){
    const bw = Math.max(b.maxX-b.minX, 1), bh = Math.max(b.maxY-b.minY, 1);
    const s = Math.min(cv.width/bw, cv.height/bh) * 0.9;
    const cx = (b.minX+b.maxX)/2, cy = (b.minY+b.maxY)/2;
    cam.scale = s; cam.ox = cv.width/2 - cx*s; cam.oy = cv.height/2 + cy*s;
  }

  function speedColor(s){
    const t = Math.max(0, Math.min(1, s/13.9)); // 0 = stopped (red) .. 1 = free-flow (green)
    const r = Math.round(230*(1-t) + 40*t), g = Math.round(70*(1-t) + 200*t);
    return 'rgb('+r+','+g+',80)';
  }

  function draw(){
    ctx.fillStyle = '#0e1116'; ctx.fillRect(0,0,cv.width,cv.height);
    if(!net){ return; }

    const frame = sampleVehicles(performance.now() - RENDER_DELAY_MS);

    // roads
    for(const lane of net.lanes){
      const p = lane.pts; if(p.length < 4) continue;
      ctx.beginPath();
      let a = w2s(p[0],p[1]); ctx.moveTo(a[0],a[1]);
      for(let i=2;i<p.length;i+=2){ const q = w2s(p[i],p[i+1]); ctx.lineTo(q[0],q[1]); }
      ctx.strokeStyle = lane.internalLane ? '#20262e' : '#39424e';
      ctx.lineWidth = Math.max(1, lane.w*cam.scale);
      ctx.lineCap = 'round'; ctx.lineJoin = 'round'; ctx.stroke();
    }

    // vehicles
    const r = Math.max(2.2, 2.4*cam.scale);
    for(const v of frame.vehicles){
      const s = w2s(v.x, v.y);
      ctx.beginPath(); ctx.arc(s[0], s[1], r, 0, 6.2832);
      ctx.fillStyle = speedColor(v.s); ctx.fill();
    }

    // obstacles
    for(const o of frame.obstacles){
      const s = w2s(o.x, o.y), k = Math.max(4, 3*cam.scale);
      ctx.strokeStyle = '#ff5c5c'; ctx.lineWidth = 2.5;
      ctx.beginPath(); ctx.moveTo(s[0]-k,s[1]-k); ctx.lineTo(s[0]+k,s[1]+k);
      ctx.moveTo(s[0]+k,s[1]-k); ctx.lineTo(s[0]-k,s[1]+k); ctx.stroke();
    }

    stat.textContent = frame.vehicles.length + ' vehicles · t=' + frame.time + 's · interpolated';
  }

  // --- interaction ---
  let drag = null;
  cv.addEventListener('mousedown', e => { drag = { x:e.clientX, y:e.clientY, ox:cam.ox, oy:cam.oy, moved:false }; });
  addEventListener('mousemove', e => {
    if(!drag) return;
    if(Math.abs(e.clientX-drag.x)+Math.abs(e.clientY-drag.y) > 3) drag.moved = true;
    cam.ox = drag.ox + (e.clientX-drag.x); cam.oy = drag.oy + (e.clientY-drag.y); draw();
  });
  addEventListener('mouseup', e => {
    if(drag && !drag.moved){ // a click, not a pan -> inject obstacle at the world point
      const rect = cv.getBoundingClientRect();
      const wp = s2w(e.clientX-rect.left, e.clientY-rect.top);
      send({ type:'obstacle', x:wp[0], y:wp[1] });
    }
    drag = null;
  });
  cv.addEventListener('wheel', e => {
    e.preventDefault();
    const f = e.deltaY < 0 ? 1.1 : 1/1.1;
    const before = s2w(e.clientX, e.clientY);
    cam.scale *= f;
    cam.ox = e.clientX - before[0]*cam.scale; cam.oy = e.clientY + before[1]*cam.scale; draw();
  }, { passive:false });
  document.getElementById('clear').addEventListener('click', () => send({ type:'clear' }));

  // --- socket ---
  let ws;
  function send(o){ if(ws && ws.readyState === 1) ws.send(JSON.stringify(o)); }
  function connect(){
    ws = new WebSocket((location.protocol==='https:'?'wss':'ws')+'://'+location.host+'/ws');
    ws.onmessage = ev => {
      const m = JSON.parse(ev.data);
      if(m.type === 'network'){ net = m; fit(m.bounds); resize(); }
      else if(m.type === 'frame'){ pushFrame(m); }
    };
    ws.onclose = () => { stat.textContent = 'disconnected — retrying…'; setTimeout(connect, 1000); };
  }
  connect();
  (function loop(){ draw(); requestAnimationFrame(loop); })();
  resize();
})();
</script>
</body>
</html>
""";
}
