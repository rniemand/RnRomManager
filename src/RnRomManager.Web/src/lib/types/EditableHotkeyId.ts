import type { HotkeyId } from '#lib/enums/index.js';

export type EditableHotkeyId = Exclude<HotkeyId, 'search'>;
