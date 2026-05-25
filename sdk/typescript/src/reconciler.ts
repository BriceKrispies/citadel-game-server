// =============================================================================
// Client-side state reconciliation — the mirror image of the server's
// snapshot/delta contract. A ServerSnapshot is a full keyframe (REPLACE the view);
// a ServerDelta is incremental (MERGE changedEntities, DELETE removedEntities) on
// top of the baseline the client last acked. A ServerCorrection overrides one
// entity authoritatively.
//
// This is the logic browser game clients use to keep a consistent world from the
// frames the server fans out, and to ack what they have applied so the server can
// advance the delta baseline (the server never assumes a frame landed without an ack).
// =============================================================================

import {
  EntityState,
  ServerSnapshot,
  ServerDelta,
  ServerCorrection,
} from "./protocol";

/** The reconstructed view: entity id -> the latest opaque payload the client holds. */
export class WorldView {
  private readonly entities = new Map<string, Uint8Array>();
  /** The highest server tick this view reflects (the next ack should confirm it). */
  public lastAppliedServerTick = -1;

  /** Applies a full keyframe: replace the entire view. */
  applySnapshot(snapshot: ServerSnapshot): void {
    this.entities.clear();
    for (const e of snapshot.entities) {
      this.entities.set(e.entityId, e.payload);
    }
    this.lastAppliedServerTick = snapshot.serverTick;
  }

  /**
   * Applies an incremental delta on top of the current baseline. The delta's
   * `fromServerTick` MUST equal this view's last applied tick — otherwise the client
   * is missing the baseline the server deltad against and must wait for a keyframe
   * (e.g. by NOT acking, which makes the server resend). Returns whether it applied.
   */
  applyDelta(delta: ServerDelta): boolean {
    if (delta.fromServerTick !== this.lastAppliedServerTick) {
      // Baseline mismatch: do not corrupt the view. Dropping the ack makes the
      // server keep resending until the client catches up (self-healing).
      return false;
    }
    for (const e of delta.changedEntities) {
      this.entities.set(e.entityId, e.payload);
    }
    for (const id of delta.removedEntities) {
      this.entities.delete(id);
    }
    this.lastAppliedServerTick = delta.toServerTick;
    return true;
  }

  /** Applies an authoritative correction for a single entity. */
  applyCorrection(correction: ServerCorrection): void {
    if (correction.authoritativeEntity) {
      const e = correction.authoritativeEntity;
      this.entities.set(e.entityId, e.payload);
    }
    if (correction.serverTick > this.lastAppliedServerTick) {
      this.lastAppliedServerTick = correction.serverTick;
    }
  }

  get(entityId: string): Uint8Array | undefined {
    return this.entities.get(entityId);
  }

  /** A stable snapshot of the current view (sorted by id) for assertions/diffing. */
  toSortedEntries(): EntityState[] {
    return [...this.entities.entries()]
      .map(([entityId, payload]) => ({ entityId, payload }))
      .sort((a, b) => (a.entityId < b.entityId ? -1 : a.entityId > b.entityId ? 1 : 0));
  }

  get size(): number {
    return this.entities.size;
  }
}
