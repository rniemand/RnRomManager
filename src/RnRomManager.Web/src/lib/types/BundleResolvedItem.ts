/** One `POST /api/bundle-files/resolve` answer item (B5); the strings are null when unavailable. */
export interface BundleResolvedItem {
	available: boolean;
	gameTitle: string | null;
	fileName: string | null;
	downloadUrl: string | null;
}
