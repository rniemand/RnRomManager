import type { ManualForm } from './ManualForm.js';

export type ManualErrors = Partial<Record<keyof ManualForm, string>>;
