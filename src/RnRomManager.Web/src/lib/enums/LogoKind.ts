import type { DiscoveryKind } from './DiscoveryKind.js';

/** B13: the lists that can have a shared logo; systems are keyed by their id. */
export type LogoKind = DiscoveryKind | 'systems';
