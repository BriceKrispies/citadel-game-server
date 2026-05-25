// =============================================================================
// Citadel realtime protocol — TypeScript types.
//
// Hand-written to mirror contracts/realtime/proto/gameserver.realtime.v1.proto
// EXACTLY (same enum numeric values, same field names in camelCase, same oneof
// shape). Enum numeric values match the proto field numbers so this model is
// drop-in compatible with a `ts-proto`-generated model.
//
// This is the SINGLE SOURCE OF TRUTH for the proto only as a CONVENIENCE TYPING.
// To regenerate the canonical, binary-accurate codec from the .proto, see
// sdk/typescript/README.md ("Regenerating from the .proto"). Until then, these
// types are kept in lockstep with the proto by hand; the byte-level wire contract
// is pinned on the server side by GoldenPacketRoundTrip + contracts/realtime/fixtures.
// =============================================================================

export enum ProtocolVersion {
  UNSPECIFIED = 0,
  V1 = 1,
}

export enum LogicalChannel {
  UNSPECIFIED = 0,
  GAMEPLAY = 1,
  PRESENCE = 2,
  CHAT = 3,
  SPECTATOR = 4,
  ADMIN = 5,
  TELEMETRY = 6,
}

export enum MessageType {
  UNSPECIFIED = 0,
  CLIENT_HELLO = 1,
  SERVER_WELCOME = 2,
  CLIENT_JOIN_ROOM = 3,
  SERVER_ROOM_JOINED = 4,
  CLIENT_INPUT_FRAME = 5,
  CLIENT_COMMAND = 6,
  CLIENT_ACK = 7,
  CLIENT_PING = 8,
  SERVER_SNAPSHOT = 9,
  SERVER_DELTA = 10,
  SERVER_CORRECTION = 11,
  SERVER_EVENT = 12,
  SERVER_PONG = 13,
  SERVER_ERROR = 14,
  GAME_MESSAGE = 15,
  CLIENT_LEAVE_ROOM = 16, // Wave 2 (additive)
}

export enum ErrorCode {
  UNSPECIFIED = 0,
  UNSUPPORTED_PROTOCOL_VERSION = 1,
  MALFORMED_FRAME = 2,
  TEXT_FRAME_NOT_ALLOWED = 3,
  UNAUTHORIZED = 4,
  INVALID_JOIN_TOKEN = 5,
  TENANT_NOT_FOUND = 6,
  GAME_NOT_FOUND = 7,
  ROOM_NOT_FOUND = 8,
  SESSION_NOT_FOUND = 9,
  PLAYER_NOT_IN_ROOM = 10,
  SEQUENCE_REJECTED = 11,
  RATE_LIMITED = 12,
  BACKPRESSURE_REJECTED = 13,
  INTERNAL_SERVER_ERROR = 14,
  UNKNOWN_GAME_MESSAGE = 15,
}

export enum DisconnectReason {
  UNSPECIFIED = 0,
  CLIENT_REQUESTED = 1,
  PROTOCOL_VIOLATION = 2,
  UNSUPPORTED_VERSION = 3,
  TEXT_FRAME = 4,
  UNAUTHORIZED = 5,
  IDLE_TIMEOUT = 6,
  SERVER_SHUTDOWN = 7,
  RATE_LIMITED = 8,
  INTERNAL_ERROR = 9,
}

// ---- Message bodies ---------------------------------------------------------

export interface ClientHello {
  requestedProtocolVersion: ProtocolVersion;
  joinToken: string;
  clientName: string;
  supportedProtocolVersions: ProtocolVersion[];
}

export interface ClientJoinRoom {
  roomId: string;
}

/** Wave 2 (additive): leave the current room without dropping the connection. */
export interface ClientLeaveRoom {
  roomId: string;
}

export interface InputCommand {
  command: string;
}

export interface ClientInputFrame {
  clientTick: number;
  commands: InputCommand[];
}

export interface ClientCommand {
  command: string;
}

export interface ClientAck {
  ackedSequence: number;
  ackedServerTick: number;
}

export interface ClientPing {
  clientTick: number;
  nonce: string;
}

export interface ServerWelcome {
  acceptedProtocolVersion: ProtocolVersion;
  sessionId: string;
  connectionId: string;
  serverTick: number;
}

export interface ServerRoomJoined {
  roomId: string;
  playerId: string;
  serverTick: number;
}

/** Legacy typed view, retained for field-number stability; no longer populated. */
export interface PlayerState {
  playerId: string;
  x: number;
}

/** An opaque, game-defined entity. The platform never interprets `payload`. */
export interface EntityState {
  entityId: string;
  payload: Uint8Array;
}

export interface ServerSnapshot {
  serverTick: number;
  players: PlayerState[]; // legacy (field 2), unused
  entities: EntityState[]; // field 3
}

export interface ServerDelta {
  fromServerTick: number;
  toServerTick: number;
  changed: PlayerState[]; // legacy (field 3), unused
  changedEntities: EntityState[]; // Wave 2 (field 4)
  removedEntities: string[]; // Wave 2 (field 5)
}

export interface ServerCorrection {
  serverTick: number;
  authoritative?: PlayerState; // legacy (field 2), unused
  ackedClientTick: number;
  authoritativeEntity?: EntityState; // Wave 2 (field 4)
}

export interface ServerEvent {
  eventType: string;
  payload: Uint8Array;
}

export interface ServerPong {
  clientTick: number;
  serverTick: number;
  nonce: string;
}

export interface ServerError {
  code: ErrorCode;
  message: string;
  fatal: boolean;
  disconnectReason: DisconnectReason;
}

export interface GameMessage {
  gameMessageType: string;
  gamePayload: Uint8Array;
  gameSchemaVersion: number;
}

/** Stable event-type strings the platform itself emits (lifecycle). */
export const ServerEventType = {
  PlayerJoined: "player_joined",
  PlayerLeft: "player_left",
  RoomTerminated: "room_terminated",
} as const;

// ---- Envelope ---------------------------------------------------------------

/**
 * The discriminated union for the envelope `payload` oneof. Exactly one of these
 * fields is set; `RealtimeEnvelope.messageType` agrees with which one.
 */
export type EnvelopePayload =
  | { clientHello: ClientHello }
  | { serverWelcome: ServerWelcome }
  | { clientJoinRoom: ClientJoinRoom }
  | { serverRoomJoined: ServerRoomJoined }
  | { clientInputFrame: ClientInputFrame }
  | { clientCommand: ClientCommand }
  | { clientAck: ClientAck }
  | { clientPing: ClientPing }
  | { serverSnapshot: ServerSnapshot }
  | { serverDelta: ServerDelta }
  | { serverCorrection: ServerCorrection }
  | { serverEvent: ServerEvent }
  | { serverPong: ServerPong }
  | { serverError: ServerError }
  | { gameMessage: GameMessage }
  | { clientLeaveRoom: ClientLeaveRoom }; // Wave 2 (field 35)

export interface RealtimeEnvelope {
  protocolVersion: ProtocolVersion;
  messageId: string;
  messageType: MessageType;
  tenantId: string;
  gameId: string;
  roomId: string;
  sessionId: string;
  playerId: string;
  connectionId: string;
  sequence: number;
  ack: number;
  clientTick: number;
  serverTick: number;
  traceId: string;
  logicalChannel: LogicalChannel;
  payload: EnvelopePayload;
}

/**
 * The binary codec the client uses to (de)serialize an envelope to protobuf wire
 * bytes. The canonical implementation is generated from the .proto (see README);
 * this interface lets the reconciliation client be written and unit-tested against
 * the typed model independently of how bytes are produced.
 */
export interface EnvelopeCodec {
  encode(envelope: RealtimeEnvelope): Uint8Array;
  decode(frame: Uint8Array): RealtimeEnvelope;
}
