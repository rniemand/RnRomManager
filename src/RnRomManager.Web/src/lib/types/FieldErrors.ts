import type { ChangePasswordForm } from './ChangePasswordForm.js';

export type FieldErrors = Partial<Record<keyof ChangePasswordForm, string>>;
