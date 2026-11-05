/**
 * Theme toggle (spec §11, D13): Light / Dark / Auto, stored in `localStorage` as `theme`. `static/theme.js` applies
 * the stored choice before first paint; this module keeps `data-bs-theme` in sync afterwards, and in Auto follows
 * `prefers-color-scheme` live.
 */

export type ThemeChoice = 'light' | 'dark' | 'auto';

const storageKey = 'theme';
const query = '(prefers-color-scheme: dark)';

function readStored(): ThemeChoice {
	try {
		const stored = localStorage.getItem(storageKey);
		return stored === 'light' || stored === 'dark' ? stored : 'auto';
	} catch {
		return 'auto';
	}
}

function apply(choice: ThemeChoice): void {
	const dark = choice === 'dark' || (choice === 'auto' && window.matchMedia(query).matches);
	document.documentElement.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
}

class Theme {
	choice = $state<ThemeChoice>('auto');
	#media: MediaQueryList | null = null;
	#onChange = () => {
		if (this.choice === 'auto') apply('auto');
	};

	/** Reads the stored choice and starts following the OS setting. Returns a cleanup function. */
	init(): () => void {
		this.choice = readStored();
		apply(this.choice);
		this.#media = window.matchMedia(query);
		this.#media.addEventListener('change', this.#onChange);
		return () => this.#media?.removeEventListener('change', this.#onChange);
	}

	set(choice: ThemeChoice): void {
		this.choice = choice;
		try {
			if (choice === 'auto') localStorage.removeItem(storageKey);
			else localStorage.setItem(storageKey, choice);
		} catch {
			// storage blocked: the choice lasts for this page only
		}
		apply(choice);
	}
}

export const theme = new Theme();
