export interface GamePatch {
	title: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	igdbId: number | null;
}
