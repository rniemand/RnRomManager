/** `GET /api/games/facets` (F1): per option, the games matching every other active filter. */
export interface GameFacets {
	systems: { id: string; count: number }[];
	regions: { region: string; count: number }[];
	letters: { letter: string; count: number }[];
}
