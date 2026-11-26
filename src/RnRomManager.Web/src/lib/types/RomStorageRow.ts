/** ROM Zip Config B12: `GET /api/admin/rom-storage`, one row per system. */
export interface RomStorageRow {
	id: string;
	displayName: string;
	/** true: ROMs inside the game ZIP when nothing is configured. */
	defaultEmbedRoms: boolean;
	/** The effective storage. */
	embedRoms: boolean;
	configurable: boolean;
	/** Games still waiting to be moved to the effective storage. */
	pendingGames: number;
	/** Timeline polish Z1: the system's games (0 when none). */
	gameCount: number;
	/** ZIP Compression Z6: the system's level (Med when none is stored). */
	zipCompression: 'Store' | 'Med' | 'High';
	/** Z5: true while the system's ROMs are stored in the ZIP. */
	compressionConfigurable: boolean;
}
