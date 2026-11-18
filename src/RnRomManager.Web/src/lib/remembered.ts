/**
 * A string remembered in `localStorage` under `key`, as a convenience: reads '' and ignores writes when storage is
 * unavailable, and saving '' removes the key.
 */
export function remembered(key: string): { read(): string; save(value: string): void } {
	return {
		read() {
			try {
				return localStorage.getItem(key) ?? '';
			} catch {
				return '';
			}
		},
		save(value) {
			try {
				if (value) localStorage.setItem(key, value);
				else localStorage.removeItem(key);
			} catch {
				// remembering is a convenience
			}
		}
	};
}
