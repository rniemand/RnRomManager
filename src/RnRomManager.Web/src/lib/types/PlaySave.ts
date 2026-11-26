/** Saves B17: a library save to load when the game starts. */
export interface PlaySave {
	saveId: string;
	saveKind: 'state' | 'battery';
}
