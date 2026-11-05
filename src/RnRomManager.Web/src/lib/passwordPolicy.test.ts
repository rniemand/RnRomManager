import { describe, expect, it } from 'vitest';
import { policyHint, validateChangePassword } from './passwordPolicy';

const valid = {
	currentPassword: 'old-password-1',
	newPassword: 'a-new-password',
	confirmPassword: 'a-new-password'
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
		const errors = validateChangePassword(
			{ ...valid, newPassword: 'Administrator', confirmPassword: 'Administrator' },
			'administrator'
		);
		expect(errors.newPassword).toMatch(/username/);
	});

	it('flags a confirmation mismatch', () =>
		expect(
			validateChangePassword({ ...valid, confirmPassword: 'a-new-passwork' }, 'boss')
				.confirmPassword
		).toMatch(/match/));

	it('the hint states the policy', () =>
		expect(policyHint).toBe('12–128 characters, and not the same as your username.'));
});
