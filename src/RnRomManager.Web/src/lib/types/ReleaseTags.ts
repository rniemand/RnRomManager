/** Release editing B7: the tags a release edit can change that show in its file name. */
export interface ReleaseTags {
	regions: string[];
	languages: string[];
	revision: string | null;
	hack: boolean;
	translated: boolean;
	version: string | null;
}
