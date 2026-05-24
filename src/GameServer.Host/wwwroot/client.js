"use strict";

// Minimal raw-WebSocket client for the Citadel authoritative-server demo.
// No framework, no build step. It speaks the same JSON envelope the kernel's
// JsonMessageCodec understands.

const PROTOCOL_VERSION = 1;

let socket = null;
let sequence = 0;
const positions = {}; // playerId -> { x, tick }

const $ = (id) => document.getElementById(id);

function log(line) {
  const el = $("log");
  el.textContent += line + "\n";
  el.scrollTop = el.scrollHeight;
}

function setStatus(text) {
  $("status").textContent = text;
}

function nextSequence() {
  sequence += 1;
  return sequence;
}

function traceId(kind) {
  return kind + "-" + Date.now() + "-" + Math.random().toString(36).slice(2, 7);
}

function renderState() {
  const body = $("state").querySelector("tbody");
  body.innerHTML = "";
  for (const player of Object.keys(positions).sort()) {
    const row = document.createElement("tr");
    const p = positions[player];
    row.innerHTML = `<td>${player}</td><td>${p.x}</td><td>${p.tick}</td>`;
    body.appendChild(row);
  }
}

function send(messageType, payload, includeRoom) {
  if (!socket || socket.readyState !== WebSocket.OPEN) {
    log("! not connected");
    return;
  }
  const envelope = {
    tenantId: $("tenant").value,
    gameId: $("game").value,
    roomId: includeRoom ? $("room").value : null,
    sessionId: null,
    playerId: $("player").value,
    protocolVersion: PROTOCOL_VERSION,
    messageType: messageType,
    sequence: nextSequence(),
    traceId: traceId(messageType),
    payload: payload,
  };
  socket.send(JSON.stringify(envelope));
  log("→ " + messageType);
}

function handleMessage(text) {
  let msg;
  try {
    msg = JSON.parse(text);
  } catch {
    log("! could not parse server message");
    return;
  }

  log("← " + msg.messageType);
  // Decodes a base64-encoded little-endian int32 (the MoveRightGame entity payload).
  function decodeInt32LE(b64) {
    const bin = atob(b64);
    return bin.charCodeAt(0) | (bin.charCodeAt(1) << 8) | (bin.charCodeAt(2) << 16) | (bin.charCodeAt(3) << 24);
  }

  switch (msg.messageType) {
    case "ServerWelcome":
      setStatus("welcomed (session " + (msg.payload?.sessionId?.value ?? "?") + ")");
      break;
    case "ServerSnapshot":
      // Entities carry game-defined opaque bytes; this demo speaks the MoveRightGame
      // payload (a little-endian int32 X), base64-encoded by the JSON dev codec.
      for (const e of (msg.payload.entities ?? [])) {
        positions[e.entityId] = { x: decodeInt32LE(e.payload), tick: msg.payload.tick };
      }
      renderState();
      break;
    case "ServerError":
      log("! ServerError " + msg.payload.code + ": " + msg.payload.message);
      break;
    default:
      log("! unknown message type: " + msg.messageType);
  }
}

function connect() {
  if (socket && socket.readyState === WebSocket.OPEN) {
    log("! already connected");
    return;
  }
  const scheme = location.protocol === "https:" ? "wss" : "ws";
  // The dev /ws endpoint derives the connection's authorized identity from these
  // query params (it trusts them — see Program.cs). They must match what we then
  // declare in ClientHello/ClientJoinRoom, or the server rejects the handshake.
  const q = new URLSearchParams({
    tenant: $("tenant").value,
    game: $("game").value,
    room: $("room").value,
    player: $("player").value,
  });
  socket = new WebSocket(`${scheme}://${location.host}/ws?${q}`);

  socket.onopen = () => {
    setStatus("connected");
    log("websocket open");
    send("ClientHello", { clientName: $("player").value }, false);
  };
  socket.onclose = () => { setStatus("disconnected"); log("websocket closed"); };
  socket.onerror = () => { log("! websocket error"); };
  socket.onmessage = (event) => handleMessage(event.data);
}

function joinRoom() {
  // ClientJoinRoom payload carries the room id as the kernel's RoomId value object.
  send("ClientJoinRoom", { roomId: { value: $("room").value } }, true);
}

function moveRight() {
  send("ClientCommand", { command: "MoveRight" }, true);
}

// Wire up controls and seed a random player id.
$("player").value = "player-" + Math.random().toString(36).slice(2, 8);
$("connect").addEventListener("click", connect);
$("join").addEventListener("click", joinRoom);
$("move").addEventListener("click", moveRight);
