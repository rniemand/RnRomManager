/** B21: a recently added release; `sizeBytes` is the release total. */
export interface ActivityRom {
	romId: number;
	gameId: string;
	gameTitle: string;
	fileName: string;
	system: string;
	sizeBytes: number;
	addedAt: string;
}
