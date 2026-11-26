import type { BundleEntry } from './BundleEntry.js';

/** The identity part of an entry: what the server resolves and what duplicates are judged by. */
export type BundleIdentity = Pick<BundleEntry, 'gameId' | 'releaseSha1' | 'member'>;
