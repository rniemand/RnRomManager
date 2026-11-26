import type { PendingSystem } from './PendingSystem.js';

/** `GET /api/ingest/removed` (Deleted ROMs D2): only systems with files in their `_removed/` folder. */
export type RemovedSystem = PendingSystem;
