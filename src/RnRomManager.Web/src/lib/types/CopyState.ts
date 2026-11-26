import type { CopyField } from './CopyField.js';

export type CopyState = Record<CopyField, { disabled: boolean; checked: boolean }>;
