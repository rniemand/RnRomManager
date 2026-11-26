export interface ManualRequest {
	action: 'Manual';
	title: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	flags: string[];
	disc?: number;
}
