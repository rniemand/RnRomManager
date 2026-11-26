import type { GameEditForm } from './GameEditForm.js';

export type GameEditErrors = Partial<Record<keyof GameEditForm, string>>;
