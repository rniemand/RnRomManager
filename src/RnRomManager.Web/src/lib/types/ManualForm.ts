export interface ManualForm {
	title: string;
	/** Comma-separated (No-Intro region and language names contain no commas). */
	regions: string;
	languages: string;
	revision: string;
	flags: string;
	/** B17: an optional disc number (disc systems only); null when empty. */
	disc: number | null;
}
