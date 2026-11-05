import { api } from '../api/client';
import { isApiError } from '../api/errors';
import type { Me } from '../api/types';

/** The signed-in user (`GET /api/auth/me`), loaded once on boot and after login/logout. */
class Auth {
	me = $state<Me | null>(null);
	loaded = $state(false);

	get isAdmin(): boolean {
		return this.me?.role === 'Admin';
	}

	/** Loads `me`; a 401 just means signed out. Other errors propagate. */
	async load(): Promise<Me | null> {
		try {
			this.me = await api.get<Me>('/api/auth/me', { noRedirect: true });
		} catch (error) {
			if (!isApiError(error) || error.status !== 401) throw error;
			this.me = null;
		} finally {
			this.loaded = true;
		}
		return this.me;
	}

	set(me: Me | null): void {
		this.me = me;
		this.loaded = true;
	}
}

export const auth = new Auth();
