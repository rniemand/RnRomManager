/** `GET /api/about` (spec §23). */
export interface About {
	version: string;
	licenses: { name: string; license: string; url: string }[];
}
