// =============================================================================
// A thin, typed realtime client. It owns the handshake/join/leave/command/ack
// protocol flow over a binary WebSocket and keeps a reconciled WorldView from the
// server's snapshots/deltas/corrections. Binary (de)serialization is delegated to
// an EnvelopeCodec (generated from the .proto — see README); the client logic here
// is transport- and codec-agnostic so it can be unit-tested against a fake codec.
// =============================================================================

import {
  EnvelopeCodec,
  LogicalChannel,
  MessageType,
  ProtocolVersion,
  RealtimeEnvelope,
  ServerEvent,
} from "./protocol";
import { WorldView } from "./reconciler";

/** A minimal binary WebSocket surface (the browser `WebSocket` satisfies this). */
export interface BinarySocket {
  send(data: Uint8Array): void;
  close(): void;
  onmessage: ((frame: Uint8Array) => void) | null;
}

export interface RealtimeClientOptions {
  tenantId: string;
  gameId: string;
  roomId: string;
  playerId: string;
  clientName?: string;
  onEvent?: (event: ServerEvent) => void;
  onError?: (code: number, message: string) => void;
}

export class RealtimeClient {
  readonly world = new WorldView();
  private sequence = 0;
  private sessionId = "";

  constructor(
    private readonly socket: BinarySocket,
    private readonly codec: EnvelopeCodec,
    private readonly options: RealtimeClientOptions,
  ) {
    this.socket.onmessage = (frame) => this.onFrame(frame);
  }

  /** Sends ClientHello. The server replies ServerWelcome. */
  hello(joinToken: string): void {
    this.send(MessageType.CLIENT_HELLO, {
      clientHello: {
        requestedProtocolVersion: ProtocolVersion.V1,
        joinToken,
        clientName: this.options.clientName ?? this.options.playerId,
        supportedProtocolVersions: [ProtocolVersion.V1],
      },
    });
  }

  join(): void {
    this.send(MessageType.CLIENT_JOIN_ROOM, { clientJoinRoom: { roomId: this.options.roomId } });
  }

  /** Wave 2: leave the room without dropping the connection. */
  leave(): void {
    this.send(MessageType.CLIENT_LEAVE_ROOM, { clientLeaveRoom: { roomId: this.options.roomId } });
  }

  /** Sends a game-defined command string (intent, never truth). */
  command(command: string): void {
    this.send(MessageType.CLIENT_COMMAND, { clientCommand: { command } });
  }

  private ack(ackedServerTick: number): void {
    this.send(MessageType.CLIENT_ACK, {
      clientAck: { ackedSequence: this.sequence, ackedServerTick },
    });
  }

  private onFrame(frame: Uint8Array): void {
    const env = this.codec.decode(frame);
    const p = env.payload as Record<string, unknown>;

    if ("serverWelcome" in p) {
      this.sessionId = env.sessionId;
      return;
    }
    if ("serverSnapshot" in p) {
      this.world.applySnapshot((p as any).serverSnapshot);
      this.ack(this.world.lastAppliedServerTick);
      return;
    }
    if ("serverDelta" in p) {
      if (this.world.applyDelta((p as any).serverDelta)) {
        this.ack(this.world.lastAppliedServerTick);
      }
      // On a baseline mismatch we deliberately do NOT ack, so the server resends.
      return;
    }
    if ("serverCorrection" in p) {
      this.world.applyCorrection((p as any).serverCorrection);
      return;
    }
    if ("serverEvent" in p) {
      this.options.onEvent?.((p as any).serverEvent);
      return;
    }
    if ("serverError" in p) {
      const err = (p as any).serverError;
      this.options.onError?.(err.code, err.message);
      return;
    }
  }

  private send(messageType: MessageType, payload: RealtimeEnvelope["payload"]): void {
    const envelope: RealtimeEnvelope = {
      protocolVersion: ProtocolVersion.V1,
      messageId: `${this.options.playerId}-${this.sequence}`,
      messageType,
      tenantId: this.options.tenantId,
      gameId: this.options.gameId,
      roomId: this.options.roomId,
      sessionId: this.sessionId,
      playerId: this.options.playerId,
      connectionId: "",
      sequence: ++this.sequence,
      ack: 0,
      clientTick: 0,
      serverTick: 0,
      traceId: `${messageType}-${this.sequence}`,
      logicalChannel: LogicalChannel.GAMEPLAY,
      payload,
    };
    this.socket.send(this.codec.encode(envelope));
  }
}
