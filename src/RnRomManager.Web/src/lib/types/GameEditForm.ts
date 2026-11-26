export interface GameEditForm {
	title: string;
	releaseYear: string;
	/** One item per line. */
	developers: string;
	publishers: string;
	genres: string;
	description: string;
	/** null = not linked, 0 = Not on IGDB, else the IGDB game id. */
	igdbId: number | null;
}
