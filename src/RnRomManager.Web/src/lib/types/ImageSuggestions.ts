import type { ImageSuggestion } from './ImageSuggestion.js';

/** `GET /api/games/{id}/image-suggestions?type=` (IGDB suggestions S8). */
export interface ImageSuggestions {
	suggestions: ImageSuggestion[];
}
