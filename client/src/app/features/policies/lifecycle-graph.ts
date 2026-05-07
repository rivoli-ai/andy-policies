// Copyright (c) Rivoli AI 2026. All rights reserved.

import { LifecycleState } from '../../shared/services/api.service';

/**
 * P9.4 (rivoli-ai/andy-policies#69) — client-side mirror of the server-side
 * lifecycle state machine. Reflects the transitions exposed by
 * `PolicyVersionsLifecycleController` (P2.3): Draft → Active (publish),
 * Active → WindingDown (winding-down), Active → Retired (retire),
 * WindingDown → Retired (retire). Retired is terminal.
 *
 * The server is the authoritative gate — illegal transitions return 409
 * regardless of what this map says. This client-side copy lets the UI
 * render the dropdown without a round-trip; if the graph changes server-side,
 * the worst case is the UI offers a transition that 409s on submit.
 */
export const LIFECYCLE_GRAPH: Record<LifecycleState, LifecycleState[]> = {
  Draft: ['Active'],
  Active: ['WindingDown', 'Retired'],
  WindingDown: ['Retired'],
  Retired: [],
};

/** Maps a target lifecycle state to the action-shaped endpoint segment used
 *  by `PolicyVersionsLifecycleController`. */
export const TRANSITION_PATH_SEGMENT: Record<LifecycleState, string | null> = {
  Active: 'publish',
  WindingDown: 'winding-down',
  Retired: 'retire',
  Draft: null, // not a transition target
};

/** User-facing labels (matches simulator visual baseline). */
export const LIFECYCLE_LABEL: Record<LifecycleState, string> = {
  Draft: 'Draft',
  Active: 'Active',
  WindingDown: 'Winding down',
  Retired: 'Retired',
};

/** Short copy describing the implication of moving INTO each state — surfaces
 *  in the dropdown so authors don't accidentally retire an active policy. */
export const TRANSITION_HINT: Record<LifecycleState, string> = {
  Active: 'Publish — auto-supersedes the previous Active version.',
  WindingDown: 'Mark for sunset — reads still resolve until retired.',
  Retired: 'Tombstone — no further transitions are possible.',
  Draft: '',
};
