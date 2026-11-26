import type { Role } from '#lib/enums/index.js';
import type { HotkeyId } from '#lib/enums/index.js';

export interface HotkeyAction {
	id: HotkeyId;
	name: string;
	defaults: string[];
	editable: boolean;
	/** null = every signed-in user. */
	role: Role | null;
}
