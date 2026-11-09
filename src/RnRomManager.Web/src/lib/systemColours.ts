/**
 * A fixed colour per system (badges and the Library Size treemap, RP3): the Bootstrap colour name and the foreground
 * `text-bg-<name>` uses on it, so labels drawn on `var(--bs-<name>)` meet AA contrast like the badges.
 */
export interface SystemColour {
	name: 'success' | 'info' | 'primary' | 'danger' | 'warning' | 'secondary';
	foreground: '#fff' | '#000';
}

export const systemColours: Record<string, SystemColour> = {
	gb: { name: 'success', foreground: '#fff' },
	gbc: { name: 'info', foreground: '#000' },
	gba: { name: 'primary', foreground: '#fff' },
	n64: { name: 'danger', foreground: '#fff' },
	nes: { name: 'warning', foreground: '#000' }
};

/** Unknown systems stay neutral. */
export const fallbackColour: SystemColour = { name: 'secondary', foreground: '#fff' };

export const systemColour = (system: string): SystemColour =>
	systemColours[system] ?? fallbackColour;
