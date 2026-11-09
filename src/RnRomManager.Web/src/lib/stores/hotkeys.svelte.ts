import { goto } from '$app/navigation';
import { resolve } from '$app/paths';
import { api } from '../api/client';
import type { Me, Page, ReviewListItem } from '../api/types';
import { actionFor, fromEvent, type Bindings, type EditableHotkeyId } from '../hotkeys';
import { auth } from './auth.svelte';

/** B23–B24: the signed-in user's hotkey bindings and the global keydown handler. */
class HotkeyStore {
	bindings = $state<Bindings | null>(null);
	#loadedFor: string | null = null;

	/**
	 * Called whenever `me` changes: loads the bindings once a user is signed in and `mustChangePassword` is false (the
	 * endpoint is blocked until then, so this also runs right after a password change); clears them otherwise.
	 */
	follow(me: Me | null): void {
		const key = me && !me.mustChangePassword ? String(me.id) : null;
		if (key === this.#loadedFor) return;
		this.#loadedFor = key;
		this.bindings = null;
		if (key !== null) void this.load(key);
	}

	async load(key: string | null = this.#loadedFor): Promise<void> {
		try {
			const { bindings } = await api.get<{ bindings: Bindings }>('/api/auth/me/hotkeys');
			if (this.#loadedFor === key) this.bindings = bindings;
		} catch {
			// Hotkeys are a convenience: without them the app works as before.
		}
	}

	/** `PUT` the bindings; the live ones change at once. Errors propagate (the modal shows them). */
	async save(bindings: Bindings): Promise<Bindings> {
		const saved = await api.put<{ bindings: Bindings }>('/api/auth/me/hotkeys', { bindings });
		this.bindings = saved.bindings;
		return saved.bindings;
	}

	/** The `window` keydown handler (B24); true when the event ran an action. */
	handle(e: KeyboardEvent): boolean {
		const me = auth.me;
		if (!me || me.mustChangePassword || !this.bindings) return false;
		if (e.repeat || e.getModifierState?.('AltGraph')) return false;
		if (document.querySelector('.modal.show')) return false;
		const combination = fromEvent(e);
		if (!combination) return false;
		const action = actionFor(combination, this.bindings, me.roles);
		if (!action) return false;
		e.preventDefault();
		void this.run(action);
		return true;
	}

	async run(action: EditableHotkeyId): Promise<void> {
		switch (action) {
			case 'nav.library':
				return goto(resolve('/'));
			case 'nav.collections':
				return goto(resolve('/collections'));
			case 'nav.ingestion':
				return goto(resolve('/ingestion'));
			case 'nav.review':
				return goto(resolve('/review'));
			case 'review.next': {
				let first: ReviewListItem | undefined;
				try {
					// The queue's default, unfiltered order (created_at, id).
					first = (
						await api.get<Page<ReviewListItem>>('/api/review?status=Pending&page=1&pageSize=1')
					).items[0];
				} catch {
					// fall back to the queue
				}
				return goto(first ? resolve('/review/[id]', { id: String(first.id) }) : resolve('/review'));
			}
		}
	}
}

export const hotkeys = new HotkeyStore();
