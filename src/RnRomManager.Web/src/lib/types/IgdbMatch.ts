/** `GET /api/games/{id}/igdb-matches` (IGDB metadata lookup L1/L4): one IGDB game, mapped to the form's rules. */
export interface IgdbMatch {
	igdbId: number;
	name: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	/** The list thumbnail (t_cover_small). */
	coverUrl: string | null;
	/** The same cover at full size (t_1080p): what Use this match adds as box art. */
	coverImageUrl: string | null;
	url: string | null;
}
