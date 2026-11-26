import { api } from '#lib/api/client.js';

/**
 * GETs `url()` whenever it or `reload()` changes, aborting the previous request; `null` loads nothing. `result` is
 * only ever the response for the current URL. A page past the last one (no items, total > 0) goes to `onPastEnd`
 * instead of `result`. Call during component init.
 */
export function useAbortableGet<T>(
	url: () => string | null,
	onPastEnd?: (result: T) => void
): { readonly result: T | null; readonly error: unknown; reload(): void } {
	let loaded = $state<{ href: string; value: T } | null>(null);
	let error = $state<unknown>(null);
	let reloads = $state(0);
	$effect(() => {
		const href = url();
		void reloads;
		if (href === null) return;
		const controller = new AbortController();
		loaded = null;
		error = null;
		api.get<T>(href, { signal: controller.signal }).then(
			(value) => {
				if (controller.signal.aborted) return;
				if (onPastEnd && isPastEnd(value)) onPastEnd(value);
				else loaded = { href, value };
			},
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
	return {
		get result() {
			return loaded !== null && loaded.href === url() ? loaded.value : null;
		},
		get error() {
			return error;
		},
		reload: () => reloads++
	};
}

function isPastEnd(value: unknown): boolean {
	const page = value as { items?: unknown; total?: unknown };
	return Array.isArray(page.items) && page.items.length === 0 && Number(page.total) > 0;
}
