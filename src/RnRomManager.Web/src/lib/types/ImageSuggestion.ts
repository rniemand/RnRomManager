/** IGDB suggestions S6: one candidate; `width`/`height` are the stored (t_1080p) size, null when unknown. */
export interface ImageSuggestion {
	previewUrl: string;
	imageUrl: string;
	gameName: string;
	width: number | null;
	height: number | null;
}
