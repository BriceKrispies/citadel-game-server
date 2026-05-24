"use strict";

// Citadel Simulation Console.
// Spawns many browser-driven game clients over the dev /ws JSON endpoint, each a real
// WebSocket that hellos, joins a grid-walk room, and streams movement commands. Every
// dot on the field is one client's *authoritative* position as broadcast back by the
// server. A separate EventSource streams aggregate server telemetry.
//
// Dev tool only: /ws and /sim/telemetry exist in the Development environment.

const TENANT = "tenant-a";
const GAME = "grid-walk";        // 2-D walker game (Up/Down/Left/Right)
const PROTOCOL_VERSION = 1;
const DIRECTIONS = ["Up", "Down", "Left", "Right"];
const SOFT_BOUND = 36;            // bias walkers back toward origin past this |x|/|y|

const $ = (id) => document.getElementById(id);

// ---- Shared authoritative world (entityId -> {x, y, tick}) ------------------
// Populated from every client's snapshots; the server is authoritative, so all
// clients in a room agree and last-write-wins is consistent.
const world = new Map();
let latestTick = 0;

let clients = [];
let nextClientId = 0;
let runId = 0;
let effectiveRoom = "arena-1";

function log(line) {
  const el = $("log");
  const ts = new Date().toLocaleTimeString();
  el.textContent += `${ts}  ${line}\n`;
  el.scrollTop = el.scrollHeight;
  // keep the log bounded
  if (el.textContent.length > 12000) el.textContent = el.textContent.slice(-9000);
}

// Decodes the grid-walk entity payload: little-endian int32 X then int32 Y, base64'd.
function decodeXY(b64) {
  const bin = atob(b64);
  const i32 = (o) =>
    (bin.charCodeAt(o) | (bin.charCodeAt(o + 1) << 8) | (bin.charCodeAt(o + 2) << 16) | (bin.charCodeAt(o + 3) << 24));
  return { x: i32(0), y: i32(4) };
}

// ---- One simulated client ---------------------------------------------------
class SimClient {
  constructor(id, room) {
    this.id = id;
    this.player = "c" + id;
    this.room = room;
    this.seq = 0;
    this.joined = false;
    this.closed = false;
    this.socket = null;
    this.moveTimer = null;
  }

  connect() {
    const scheme = location.protocol === "https:" ? "wss" : "ws";
    // The dev /ws endpoint trusts this identity and requires our hello/join to match it.
    const q = new URLSearchParams({ tenant: TENANT, game: GAME, room: this.room, player: this.player });
    this.socket = new WebSocket(`${scheme}://${location.host}/ws?${q}`);
    this.socket.onopen = () => this.send("ClientHello", { clientName: this.player }, false);
    this.socket.onmessage = (e) => this.onMessage(e.data);
    this.socket.onclose = () => { this.joined = false; this.stopMoving(); refreshSummary(); };
    this.socket.onerror = () => {};
  }

  send(messageType, payload, includeRoom) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) return;
    this.seq += 1;
    // Flat envelope: identity fields are bare strings (see JsonMessageCodec).
    this.socket.send(JSON.stringify({
      tenantId: TENANT,
      gameId: GAME,
      roomId: includeRoom ? this.room : null,
      sessionId: null,
      playerId: this.player,
      protocolVersion: PROTOCOL_VERSION,
      messageType,
      sequence: this.seq,
      traceId: `${messageType}-${this.player}-${this.seq}`,
      payload,
    }));
  }

  onMessage(text) {
    let msg;
    try { msg = JSON.parse(text); } catch { return; }

    switch (msg.messageType) {
      case "ServerWelcome":
        // The join payload carries the room id as the kernel's RoomId value object.
        this.send("ClientJoinRoom", { roomId: { value: this.room } }, true);
        this.joined = true;
        this.startMoving();
        refreshSummary();
        break;
      case "ServerSnapshot":
        latestTick = Math.max(latestTick, msg.payload.tick ?? 0);
        for (const e of (msg.payload.entities ?? [])) {
          const { x, y } = decodeXY(e.payload);
          world.set(e.entityId, { x, y, tick: msg.payload.tick });
        }
        break;
      case "ServerError":
        log(`! ${this.player} ServerError ${msg.payload.code}: ${msg.payload.message}`);
        break;
    }
  }

  startMoving() {
    this.stopMoving();
    const movesPerSec = currentRate();
    if (movesPerSec <= 0) return;
    const intervalMs = 1000 / movesPerSec;
    this.moveTimer = setInterval(() => this.step(), intervalMs);
  }

  stopMoving() {
    if (this.moveTimer) { clearInterval(this.moveTimer); this.moveTimer = null; }
  }

  step() {
    if (!this.joined) return;
    this.send("ClientCommand", { command: this.chooseDirection() }, true);
  }

  // Biased random walk: roam freely, but when the authoritative position drifts past the
  // soft bound, steer back toward origin so the swarm stays on screen. The bias reads the
  // server's truth for this player, not a locally-predicted position.
  chooseDirection() {
    const me = world.get(this.player);
    if (me && Math.random() < 0.7) {
      if (Math.abs(me.x) > SOFT_BOUND || Math.abs(me.y) > SOFT_BOUND) {
        if (Math.abs(me.x) >= Math.abs(me.y)) return me.x > 0 ? "Left" : "Right";
        return me.y > 0 ? "Down" : "Up";
      }
    }
    return DIRECTIONS[(Math.random() * DIRECTIONS.length) | 0];
  }

  close() {
    this.closed = true;
    this.stopMoving();
    try { this.socket && this.socket.close(); } catch {}
  }
}

// ---- Spawning ---------------------------------------------------------------
function currentRate() {
  return Math.max(0, Math.min(60, Number($("rate").value) || 0));
}

function addClients(count) {
  for (let i = 0; i < count; i++) {
    const c = new SimClient(nextClientId++, effectiveRoom);
    clients.push(c);
    // A tiny stagger keeps the browser responsive while still bursting connections.
    setTimeout(() => { if (!c.closed) c.connect(); }, i * 8);
  }
  refreshSummary();
}

function spawn() {
  stopAll();
  world.clear();
  latestTick = 0;
  clients = [];
  nextClientId = 0;
  runId += 1;
  // Fresh room per run so prior (server-persisted) walkers don't linger as ghosts.
  effectiveRoom = `${($("room").value || "arena").trim()}-${runId}`;
  const n = Math.max(1, Math.min(200, Number($("clients").value) || 1));
  log(`spawning ${n} clients into ${effectiveRoom}`);
  addClients(n);
  $("stop").disabled = false;
}

function stopAll() {
  for (const c of clients) c.close();
  log(clients.length ? `stopped ${clients.length} clients` : "");
  $("stop").disabled = true;
  refreshSummary();
}

function refreshSummary() {
  const joined = clients.filter((c) => c.joined).length;
  const open = clients.filter((c) => c.socket && c.socket.readyState === WebSocket.OPEN).length;
  $("connSummary").textContent =
    `${clients.length} clients · ${open} connected · ${joined} joined · room ${effectiveRoom}`;
}

// When the rate changes, retune every active client's move timer.
function retuneRates() {
  for (const c of clients) if (c.joined) c.startMoving();
}

// ---- Field rendering --------------------------------------------------------
const field = $("field");
const fctx = field.getContext("2d");

function colorFor(player) {
  let h = 0;
  for (let i = 0; i < player.length; i++) h = (h * 31 + player.charCodeAt(i)) & 0xffff;
  return `hsl(${h % 360}, 70%, 60%)`;
}

function resizeCanvas(canvas) {
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    canvas.getContext("2d").setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  return { w, h };
}

function renderField() {
  const { w, h } = resizeCanvas(field);
  fctx.clearRect(0, 0, w, h);

  const cx = w / 2, cy = h / 2;
  const scale = Math.max(4, Math.min(w, h) / (SOFT_BOUND * 2 + 8));

  // grid + axes
  fctx.strokeStyle = "#161b22";
  fctx.lineWidth = 1;
  const stepPx = scale * 4;
  for (let gx = cx % stepPx; gx < w; gx += stepPx) { fctx.beginPath(); fctx.moveTo(gx, 0); fctx.lineTo(gx, h); fctx.stroke(); }
  for (let gy = cy % stepPx; gy < h; gy += stepPx) { fctx.beginPath(); fctx.moveTo(0, gy); fctx.lineTo(w, gy); fctx.stroke(); }
  fctx.strokeStyle = "#21262d";
  fctx.beginPath(); fctx.moveTo(cx, 0); fctx.lineTo(cx, h); fctx.moveTo(0, cy); fctx.lineTo(w, cy); fctx.stroke();

  // dots
  for (const [player, p] of world) {
    const px = cx + p.x * scale;
    const py = cy - p.y * scale;
    if (px < -10 || px > w + 10 || py < -10 || py > h + 10) continue;
    fctx.fillStyle = colorFor(player);
    fctx.beginPath();
    fctx.arc(px, py, 4.5, 0, Math.PI * 2);
    fctx.fill();
    if (world.size <= 40) {
      fctx.fillStyle = "#8b949e";
      fctx.font = "10px ui-monospace, monospace";
      fctx.fillText(player, px + 6, py - 6);
    }
  }

  // HUD
  fctx.fillStyle = "#c9d1d9";
  fctx.font = "12px ui-monospace, monospace";
  fctx.fillText(`entities in view: ${world.size}   tick: ${latestTick}`, 8, 16);

  requestAnimationFrame(renderField);
}

// ---- Telemetry feed (SSE) ---------------------------------------------------
const sparkEntities = $("sparkEntities");
const sparkTick = $("sparkTick");
const entHist = [];
const tickHist = [];
const HIST = 60;

function fmt(n) {
  if (n >= 1000) return (n / 1000).toFixed(n >= 10000 ? 0 : 1) + "k";
  return Math.round(n).toString();
}

function drawSpark(canvas, data, color) {
  const { w, h } = resizeCanvas(canvas);
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, w, h);
  if (data.length < 2) return;
  const max = Math.max(1, ...data);
  ctx.strokeStyle = color;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  data.forEach((v, i) => {
    const x = (i / (HIST - 1)) * w;
    const y = h - (v / max) * (h - 4) - 2;
    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  });
  ctx.stroke();
  ctx.fillStyle = "#8b949e";
  ctx.font = "9px ui-monospace, monospace";
  ctx.fillText(`max ${fmt(max)}`, 4, 10);
}

function setMetric(id, value, cls) {
  const el = $(id);
  el.textContent = value;
  el.className = "v" + (cls ? " " + cls : "");
}

function applyTelemetry(r) {
  setMetric("m_entities", fmt(r.entitiesPerSecond), "good");
  setMetric("m_msgsout", fmt(r.messagesOutPerSecond));
  setMetric("m_snapshots", fmt(r.snapshotsPerSecond));
  setMetric("m_msgsin", fmt(r.messagesInPerSecond));
  setMetric("m_tick", r.tickMeanMs.toFixed(1), r.tickMeanMs > 80 ? "bad" : r.tickMeanMs > 40 ? "warn" : "good");
  setMetric("m_tickmax", r.tickMaxMs.toFixed(1), r.tickMaxMs > 100 ? "warn" : "");
  setMetric("m_accepted", fmt(r.commandsAcceptedPerSecond));
  setMetric("m_backpressure", fmt(r.backpressureRejectionsPerSecond), r.backpressureRejectionsPerSecond > 0 ? "bad" : "");
  setMetric("m_conns", fmt(r.connectionsOpened));
  setMetric("m_dropped", fmt(r.connectionsDropped), r.connectionsDropped > 0 ? "warn" : "");

  entHist.push(r.entitiesPerSecond); if (entHist.length > HIST) entHist.shift();
  tickHist.push(r.tickMeanMs); if (tickHist.length > HIST) tickHist.shift();
  drawSpark(sparkEntities, entHist, "#3fb950");
  drawSpark(sparkTick, tickHist, "#58a6ff");
}

function connectTelemetry() {
  const es = new EventSource("/sim/telemetry");
  es.onopen = () => { $("teleDot").classList.add("live"); };
  es.onmessage = (e) => {
    try { applyTelemetry(JSON.parse(e.data)); } catch {}
  };
  es.onerror = () => { $("teleDot").classList.remove("live"); }; // EventSource auto-reconnects
}

// ---- Wire up ----------------------------------------------------------------
$("spawn").addEventListener("click", spawn);
$("addClients").addEventListener("click", () => { if (clients.length) addClients(Math.max(1, Math.min(50, Number($("clients").value) || 1))); else spawn(); });
$("stop").addEventListener("click", stopAll);
$("rate").addEventListener("change", retuneRates);
window.addEventListener("beforeunload", stopAll);

refreshSummary();
connectTelemetry();
requestAnimationFrame(renderField);
