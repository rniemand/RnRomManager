/**
 * A fixed colour per system (badges and the Library Size treemap, RP3): the Bootstrap colour name and the foreground
 * `text-bg-<name>` uses on it, so labels drawn on `var(--bs-<name>)` meet AA contrast like the badges.
 */
export interface SystemColour {
	name:
		| 'success'
		| 'info'
		| 'primary'
		| 'danger'
		| 'warning'
		| 'secondary'
		| 'snes'
		| 'vb'
		| 'a2600'
		| 'a5200'
		| 'a7800'
		| 'lynx'
		| 'jaguar'
		| 'coleco'
		| 'gg'
		| 'sms'
		| 'md'
		| 's32x'
		| 'tg16'
		| 'switch'
		| 'psx'
		| 'psp'
		| 'gc'
		| 'wii'
		| 'x360'
		| 'saturn';
	foreground: '#fff' | '#000';
}

export const systemColours: Record<string, SystemColour> = {
	gb: { name: 'success', foreground: '#fff' },
	gbc: { name: 'info', foreground: '#000' },
	gba: { name: 'primary', foreground: '#fff' },
	n64: { name: 'danger', foreground: '#fff' },
	nes: { name: 'warning', foreground: '#000' },
	snes: { name: 'snes', foreground: '#fff' },
	vb: { name: 'vb', foreground: '#000' },
	a2600: { name: 'a2600', foreground: '#fff' },
	a5200: { name: 'a5200', foreground: '#fff' },
	a7800: { name: 'a7800', foreground: '#000' },
	lynx: { name: 'lynx', foreground: '#000' },
	jaguar: { name: 'jaguar', foreground: '#fff' },
	coleco: { name: 'coleco', foreground: '#fff' },
	gg: { name: 'gg', foreground: '#fff' },
	sms: { name: 'sms', foreground: '#fff' },
	md: { name: 'md', foreground: '#fff' },
	s32x: { name: 's32x', foreground: '#000' },
	tg16: { name: 'tg16', foreground: '#fff' },
	switch: { name: 'switch', foreground: '#fff' },
	psx: { name: 'psx', foreground: '#fff' },
	psp: { name: 'psp', foreground: '#fff' },
	gc: { name: 'gc', foreground: '#000' },
	wii: { name: 'wii', foreground: '#000' },
	x360: { name: 'x360', foreground: '#000' },
	saturn: { name: 'saturn', foreground: '#fff' }
};

/** Unknown systems stay neutral. */
export const fallbackColour: SystemColour = { name: 'secondary', foreground: '#fff' };

export const systemColour = (system: string): SystemColour =>
	systemColours[system] ?? fallbackColour;
