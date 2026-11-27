import type { SystemColour } from '#lib/types/index.js';

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
	tgcd: { name: 'tgcd', foreground: '#fff' },
	pcfx: { name: 'pcfx', foreground: '#fff' },
	sgx: { name: 'sgx', foreground: '#fff' },
	dos: { name: 'dos', foreground: '#fff' },
	mame2003: { name: 'mame2003', foreground: '#fff' },
	fbneo: { name: 'fbneo', foreground: '#fff' },
	ngp: { name: 'ngp', foreground: '#fff' },
	ngpc: { name: 'ngpc', foreground: '#000' },
	ws: { name: 'ws', foreground: '#000' },
	wsc: { name: 'wsc', foreground: '#000' },
	pc2: { name: 'pc2', foreground: '#000' },
	switch: { name: 'switch', foreground: '#fff' },
	psx: { name: 'psx', foreground: '#fff' },
	psp: { name: 'psp', foreground: '#fff' },
	gc: { name: 'gc', foreground: '#000' },
	wii: { name: 'wii', foreground: '#000' },
	x360: { name: 'x360', foreground: '#000' },
	saturn: { name: 'saturn', foreground: '#fff' },
	nds: { name: 'nds', foreground: '#000' },
	segacd: { name: 'segacd', foreground: '#fff' },
	'3do': { name: '3do', foreground: '#000' },
	c64: { name: 'c64', foreground: '#fff' },
	pet: { name: 'pet', foreground: '#fff' },
	plus4: { name: 'plus4', foreground: '#fff' },
	vic20: { name: 'vic20', foreground: '#fff' },
	c128: { name: 'c128', foreground: '#fff' },
	amiga: { name: 'amiga', foreground: '#fff' },
	fds: { name: 'fds', foreground: '#fff' },
	'3ds': { name: '3ds', foreground: '#000' },
	satellaview: { name: 'satellaview', foreground: '#fff' },
	sufami: { name: 'sufami', foreground: '#fff' }
};

/** Unknown systems stay neutral. */
export const fallbackColour: SystemColour = { name: 'secondary', foreground: '#fff' };

export const systemColour = (system: string): SystemColour =>
	systemColours[system] ?? fallbackColour;
