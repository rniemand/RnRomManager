import { describe, expect, it } from 'vitest';
import type { Me } from './api/types';
import { guard, safeReturnUrl } from './guard';

const user: Me = { id: 2, username: 'plain', roles: [], mustChangePassword: false };

describe('guard', () => {
	it('sends a signed-out visitor to login with a returnUrl', () => {
		expect(guard('/games/1', '?x=1', null)).toBe('/login?returnUrl=%2Fgames%2F1%3Fx%3D1');
	});

	it('lets anyone reach login', () => {
		expect(guard('/login', '', null)).toBeNull();
		expect(guard('/login', '', user)).toBeNull();
	});

	it('forces a password change before anything else', () => {
		const mustChange = { ...user, mustChangePassword: true };
		expect(guard('/', '', mustChange)).toBe('/change-password');
		expect(guard('/change-password', '', mustChange)).toBeNull();
	});

	it('lets a signed-in user through (Admin pages check the role themselves)', () => {
		expect(guard('/', '', user)).toBeNull();
		expect(guard('/admin/users', '', user)).toBeNull();
	});
});

describe('safeReturnUrl', () => {
	it.each([
		['/games/1?x=1', '/games/1?x=1'],
		[null, '/'],
		['', '/'],
		['https://evil.example', '/'],
		['//evil.example', '/'],
		['/\\evil.example', '/'],
		['games', '/']
	])('%s -> %s', (value, expected) => expect(safeReturnUrl(value)).toBe(expected));
});
