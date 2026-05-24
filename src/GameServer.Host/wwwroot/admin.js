"use strict";

// Citadel Admin Console: "drop in" to a live room and watch exactly what the server is
// broadcasting — authoritative entities (plotted by their generic relevance key, so it
// works for any game) plus each connected session's lag. Strictly read-only.
//
// Auth: the admin APIs require `Authorization: Bearer <key>` (a control-plane key). The
// browser EventSource API can't set headers, so the live stream is read with fetch() +
// a streaming body reader, which can — keeping the endpoint properly authenticated.

const $ = (id) => document.getElementById(id);
let streamAbort = null;     // AbortController for the active drop-in stream
const world = new Map();    // entityId -> { x, y }

function authHeaders() {
  return { Authorization: `Bearer ${$("key").value.trim()}` };
}

async function listRooms() {
  $("roomsMsg").textContent = "loading…";
  let data;
  try {
    const resp = await fetch("/api/v1/admin/rooms", { headers: authHeaders() });
    if (!resp.ok) { $("roomsMsg").textContent = `error ${resp.status} (${resp.statusText}) — check the admin key`; return; }
    data = await resp.json();
  } catch (e) {
    $("roomsMsg").textContent = "request failed: " + e;
    return;
  }

  const rooms = $("rooms").querySelector("tbody");
  rooms.innerHTML = "";
  for (const r of data.rooms) {
    const tr = document.createElement("tr");
    tr.className = "room";
    tr.innerHTML = `<td>${r.tenantId}/${r.roomId}</td><td>${r.subscriberCount}</td><td>${r.tick}</td>`;
    tr.addEventListener("click", () => dropIn(r.tenantId, r.roomId));
    rooms.appendChild(tr);
  }
  $("roomsMsg").textContent = data.rooms.length ? "" : "no active rooms (connect some clients first)";

  const hottest = $("hottest").querySelector("tbody");
  hottest.innerHTML = "";
  for (const h of data.hottest) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td>${h.room}</td><td>${h.meanMs.toFixed(1)}</td><td>${h.maxMs.toFixed(1)}</td>`;
    hottest.appendChild(tr);
  }
}

function stopStream() {
  if (streamAbort) { streamAbort.abort(); streamAbort = null; }
  $("stop").disabled = true;
}

async function dropIn(tenant, room) {
  stopStream();
  world.clear();
  $("target").textContent = `${tenant}/${room}`;
  $("stop").disabled = false;

  const ctrl = new AbortController();
  streamAbort = ctrl;
  const url = `/api/v1/admin/rooms/${encodeURIComponent(tenant)}/${encodeURIComponent(room)}/observe`;

  let resp;
  try {
    resp = await fetch(url, { headers: authHeaders(), signal: ctrl.signal });
  } catch { return; }
  if (!resp.ok || !resp.body) { $("target").textContent = `${tenant}/${room} — error ${resp.status}`; return; }

  // Parse the SSE stream manually (fetch streaming, so we can send the auth header).
  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      let sep;
      while ((sep = buffer.indexOf("\n\n")) >= 0) {
        const frame = buffer.slice(0, sep);
        buffer = buffer.slice(sep + 2);
        handleFrame(frame, tenant, room);
      }
    }
  } catch {
    // aborted (Stop / new selection) — fall through
  }
}

function handleFrame(frame, tenant, room) {
  if (frame.startsWith("event: gone")) {
    $("target").textContent = `${tenant}/${room} — room closed`;
    stopStream();
    return;
  }
  const line = frame.split("\n").find((l) => l.startsWith("data:"));
  if (!line) return;
  let obs;
  try { obs = JSON.parse(line.slice(5).trim()); } catch { return; }
  render(obs);
}

function render(obs) {
  $("tick").textContent = obs.tick;

  world.clear();
  for (const e of obs.entities) {
    world.set(e.entityId, { x: e.x, y: e.y });
  }
  drawField();

  const viewers = $("viewers").querySelector("tbody");
  viewers.innerHTML = "";
  for (const v of obs.viewers) {
    const tr = document.createElement("tr");
    const cls = v.pendingSnapshots === 0 ? "lag-ok" : v.pendingSnapshots < 5 ? "lag-warn" : "lag-bad";
    tr.innerHTML =
      `<td>${v.connectionId.slice(0, 12)}</td><td>${v.playerId ?? "—"}</td>` +
      `<td class="${cls}">${v.pendingSnapshots}</td>`;
    viewers.appendChild(tr);
  }
}

const field = $("field");
const fctx = field.getContext("2d");

function colorFor(id) {
  let h = 0;
  for (let i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) & 0xffff;
  return `hsl(${h % 360}, 70%, 60%)`;
}

function drawField() {
  const dpr = window.devicePixelRatio || 1;
  const w = field.clientWidth, h = field.clientHeight;
  if (field.width !== Math.round(w * dpr)) { field.width = Math.round(w * dpr); field.height = Math.round(h * dpr); fctx.setTransform(dpr, 0, 0, dpr, 0, 0); }
  fctx.clearRect(0, 0, w, h);

  // Auto-scale to fit the spread of entities around the origin.
  let extent = 8;
  for (const p of world.values()) extent = Math.max(extent, Math.abs(p.x), Math.abs(p.y));
  const cx = w / 2, cy = h / 2;
  const scale = Math.min(w, h) / (extent * 2 + 4);

  fctx.strokeStyle = "#21262d";
  fctx.beginPath(); fctx.moveTo(cx, 0); fctx.lineTo(cx, h); fctx.moveTo(0, cy); fctx.lineTo(w, cy); fctx.stroke();

  for (const [id, p] of world) {
    const px = cx + p.x * scale, py = cy - p.y * scale;
    fctx.fillStyle = colorFor(id);
    fctx.beginPath(); fctx.arc(px, py, 5, 0, Math.PI * 2); fctx.fill();
    fctx.fillStyle = "#8b949e";
    fctx.font = "10px ui-monospace, monospace";
    fctx.fillText(id, px + 7, py - 7);
  }
}

$("refresh").addEventListener("click", listRooms);
$("stop").addEventListener("click", stopStream);
window.addEventListener("beforeunload", stopStream);
listRooms();
