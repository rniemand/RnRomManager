import type { RomDetail } from './RomDetail.js';
import type { ImageInfo } from './ImageInfo.js';

/** `GET /api/games/{id}` (spec §23). */
export interface GameDetail {
	id: string;
	system: string;
	/** The handler's display name (GP2). */
	systemName: string;
	/** Browser emulators B2: the EmulatorJS core that plays this system, or null. */
	browserCore: string | null;
	/** Release editing B11: a disc (FileSets) system, whose releases keep their file names. */
	fileSets: boolean;
	title: string;
	displayTitle: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	hasDefaultBoxArt: boolean;
	rating: number | null;
	favourite: boolean;
	/** Saves in the game's ZIP. */
	saveCount: number;
	/** J1: the linked IGDB game id; null = not linked, 0 = marked Not on IGDB. */
	igdbId: number | null;
	/** P3: whether the IGDB keys are set (Lookup Info and the banner need them). */
	igdbConfigured: boolean;
	/** R4: the IGDB rating from the last refresh; null until one stored a score. */
	igdbRating?: { score: number; votes: number; syncedAt: string } | null;
	metadata: { createdAt: string; updatedAt: string };
	roms: RomDetail[];
	images: ImageInfo[];
}
