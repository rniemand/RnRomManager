import type { Bindings } from './Bindings.js';
import type { EditableHotkeyId } from './EditableHotkeyId.js';

/** `GET`/`PUT /api/auth/me/hotkeys` (B23). `implicitUnassigned`: actions whose default yields to an older override. */
export interface HotkeysResponse {
	bindings: Bindings;
	implicitUnassigned: EditableHotkeyId[];
}
