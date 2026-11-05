import { describe, expect, it } from 'vitest';
import { passwordError, policyHint, usernameError, validateChangePassword } from './passwordPolicy';
import { fakePassword } from './testing/fakePassword';

const next = fakePassword('next');

const valid = {
	currentPassword: fakePassword('current'),
	newPassword: next,
	confirmPassword: next
};

describe('change-password validation (§9.1)', () => {
	it('accepts a valid form', () => expect(validateChangePassword(valid, 'boss')).toEqual({}));

	it('requires the current password', () =>
		expect(validateChangePassword({ ...valid, currentPassword: '' }, 'boss')).toHaveProperty(
			'currentPassword'
		));

	it.each([
		['x'.repeat(11), true],
		['x'.repeat(12), false],
		['x'.repeat(128), false],
		['x'.repeat(129), true]
	])('length %#: invalid=%s', (password, invalid) => {
		const errors = validateChangePassword(
			{ ...valid, newPassword: password, confirmPassword: password },
			'boss'
		);
		expect('newPassword' in errors).toBe(invalid);
	});

	it('rejects the username, case-insensitively', () => {
		const username = 'administrator';
		const errors = validateChangePassword(
			{ ...valid, newPassword: username.toUpperCase(), confirmPassword: username.toUpperCase() },
			username
		);
		expect(errors.newPassword).toMatch(/username/);
	});

	it('flags a confirmation mismatch', () =>
		expect(
			validateChangePassword({ ...valid, confirmPassword: next + '!' }, 'boss').confirmPassword
		).toMatch(/match/));

	it('the hint states the policy', () =>
		expect(policyHint).toBe('12–128 characters, and not the same as your username.'));
});

describe('admin user rules', () => {
	it('usernames: 3–64 of [A-Za-z0-9._-]', () => {
		expect(usernameError('ab')).toBeDefined();
		expect(usernameError('a'.repeat(65))).toBeDefined();
		expect(usernameError('bad name')).toBeDefined();
		expect(usernameError('j.doe_2-x')).toBeUndefined();
	});

	it('passwords: 12–128 chars and not the username', () => {
		expect(passwordError('short', 'bob')).toBeDefined();
		expect(passwordError('Abcdefghijkl', 'abcdefghijkl')).toBeDefined();
		expect(passwordError(fakePassword('ok'), 'bob')).toBeUndefined();
	});
});
