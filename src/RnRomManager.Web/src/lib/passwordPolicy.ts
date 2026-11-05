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
	const newPassword = passwordError(form.newPassword, username);
	if (newPassword) errors.newPassword = newPassword;
	if (form.confirmPassword !== form.newPassword)
		errors.confirmPassword = "The passwords don't match.";
	return errors;
}

/** The §9.1 rules for a new password; undefined when it's allowed. */
export function passwordError(password: string, username: string): string | undefined {
	if (password.length < minLength || password.length > maxLength)
		return `Use ${minLength}–${maxLength} characters.`;
	if (password.toLowerCase() === username.toLowerCase())
		return 'Must not be the same as the username.';
	return undefined;
}

/** §9.1: 3–64 characters `[A-Za-z0-9._-]`. */
export function usernameError(username: string): string | undefined {
	return /^[A-Za-z0-9._-]{3,64}$/.test(username)
		? undefined
		: "Use 3–64 characters: letters, digits, '.', '_' or '-'.";
}
