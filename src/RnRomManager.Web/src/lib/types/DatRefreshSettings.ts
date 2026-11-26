/** B28: `GET/PUT /api/admin/dats/settings`. */
export interface DatRefreshSettings {
	enabled: boolean;
	/** `HH:mm` in the server's time zone. */
	time: string;
	/** The container's `TZ` id. */
	timeZone: string;
	nextRunAt: string | null;
}
