/** One item of `GET /api/games` (spec §23). */
export interface GameListItem {
	id: string;
	system: string;
	displayTitle: string;
	releaseYear: number | null;
	regions: string[];
	romCount: number;
	/** ROM size of the primary release (LP1); null when the game has none. */
	primarySizeBytes: number | null;
	boxArtUrl: string | null;
	/** The handler's display name, or the id when it isn't registered. */
	systemName: string;
	/** The handler's short name (`SNES`) for game pickers, or the id when it isn't registered. */
	systemShortName: string;
	/** 1–5; null = not rated. Shared per game. */
	rating: number | null;
	favourite: boolean;
	/** With `q`: the metadata value that matched when no word was in a title (S3); null otherwise. */
	matchedOn: {
		field: 'developer' | 'publisher' | 'genre' | 'dlc' | 'update' | 'sha1' | 'md5';
		value: string;
	} | null;
	/** Roms flagged `Update` (C1). */
	updateCount: number;
	/** DLC B7: releases flagged `DLC` and not `Update`. */
	dlcCount: number;
	/** Saves in the game's ZIP. */
	saveCount: number;
	/** S1: the viewer's own saves (Admins too). */
	ownSaveCount: number;
	/** SV2: the viewer's own save states; null when unknown (counted before the field existed). */
	ownStateSaveCount: number | null;
	/** Primary release to quick play (C2); null when the system has no browser core or the release is external. */
	playRomId: number | null;
	playRomFileName: string | null;
	/** Sega CD B6: the quick-play release is external, so its play URL carries `disc=1`. */
	playDisc: boolean;
	/** The system handler's browser core, null when it has none. */
	browserCore: string | null;
	/** Timeline T9: the game's IGDB id; null (or 0) when not linked. */
	igdbId: number | null;
	/** GS1: the stored IGDB rating (0–100), null when none. */
	igdbRating: number | null;
}
