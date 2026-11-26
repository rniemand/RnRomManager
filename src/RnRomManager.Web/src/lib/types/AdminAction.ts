import type { RomDetail } from './RomDetail.js';

export type AdminAction =
	| { kind: 'edit'; lookup?: boolean }
	| { kind: 'retry' }
	| { kind: 'merge' }
	| { kind: 'deleteGame' }
	| { kind: 'move'; rom: RomDetail }
	| { kind: 'primary'; rom: RomDetail }
	| { kind: 'deleteRom'; rom: RomDetail };
