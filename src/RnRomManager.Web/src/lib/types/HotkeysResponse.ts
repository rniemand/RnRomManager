import type { Bindings, EditableHotkeyId } from '#lib/hotkeys.js';

/** `GET`/`PUT /api/auth/me/hotkeys` (B23). `implicitUnassigned`: actions whose default yields to an older override. */
export interface HotkeysResponse {
	bindings: Bindings;
	implicitUnassigned: EditableHotkeyId[];
}
