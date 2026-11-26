export interface SystemInfo {
	id: string;
	displayName: string;
	/** The handler's short name (`GB`), for system badges (L3). */
	shortName: string;
	gameCount: number;
	romCount: number;
	regions: string[];
	/** B15: the shared logo's `/api/discovery/systems/logo?key=<id>&v=…`, or null without one. */
	logoUrl: string | null;
}
