/** Spec §9.1 password policy, mirrored client-side for instant feedback (the API checks it again). */

export const minLength = 12;
export const maxLength = 128;

export const policyHint = `${minLength}–${maxLength} characters, and not the same as your username.`;

export interface ChangePasswordForm {
	currentPassword: string;
	newPassword: string;
	confirmPassword: string;
}

export type FieldErrors = Partial<Record<keyof ChangePasswordForm, string>>;

/** Errors per field; empty when the form can be sent. */
export function validateChangePassword(form: ChangePasswordForm, username: string): FieldErrors {
	const errors: FieldErrors = {};
	if (!form.currentPassword) errors.currentPassword = 'Enter your current password.';
	if (form.newPassword.length < minLength || form.newPassword.length > maxLength)
		errors.newPassword = `Use ${minLength}–${maxLength} characters.`;
	else if (form.newPassword.toLowerCase() === username.toLowerCase())
		errors.newPassword = 'Must not be the same as your username.';
	if (form.confirmPassword !== form.newPassword)
		errors.confirmPassword = "The passwords don't match.";
	return errors;
}
