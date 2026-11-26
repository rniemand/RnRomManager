/** `GET /api/discovery/{kind}` item (discovery B3): one publisher or genre across systems. */
export interface DiscoveryGroup {
	/** Lowercase hex SHA-256 of the normalized name. */
	key: string;
	name: string;
	gameCount: number;
	/** The shared logo's `…/logo?key=…&v=…`, or null without one (B6, B15). */
	logoUrl?: string | null;
}
