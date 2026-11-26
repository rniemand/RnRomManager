import type { EmulatorOs } from '#lib/enums/index.js';

export interface Emulator {
	name: string;
	url: string;
	os: EmulatorOs[];
	note?: string;
}
