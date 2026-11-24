// Browser emulators B5 step 1: the play page's query, validated before anything is fetched or loaded.

/**
 * Sega CD B6: `disc` is true for an external release (`disc=1`), which loads from play-disc whatever the core.
 * Saves B17: `save` is the library save to load at game start (`save` + `saveKind`), or null.
 * @typedef {{ id: string, kind: 'state' | 'battery' }} PlaySave
 * @typedef {{ game: string, rom: number, core: string, name: string, disc: boolean, set: boolean, save: PlaySave | null }} PlayParams
 */

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const cores = [
	'gambatte',
	'mgba',
	'handy',
	'virtualjaguar',
	'beetle_vb',
	'a5200',
	'prosystem',
	'stella2014',
	'gearcoleco',
	'smsplus',
	'fceumm',
	'pcsx_rearmed',
	'ppsspp',
	'genesis_plus_gx',
	'picodrive',
	'yabause',
	'snes9x',
	'desmume',
	'dosbox_pure',
	'opera',
	'vice_x64sc',
	'vice_xpet',
	'vice_xvic',
	'puae',
	'mednafen_pce',
	'mednafen_ngp',
	'mupen64plus_next',
	'azahar'
];

/**
 * PSP B3, MS-DOS P5, 3DS G2: the cores that need threads and so run only on their own cross-origin isolated page, keyed to
 * that page's `data-player` (never in the modal's play.html).
 * @type {Record<string, string>}
 */
export const isolatedPlayers = { ppsspp: 'psp', dosbox_pure: 'dos', azahar: '3ds' };

/**
 * @param {string} search `location.search`
 * @param {string} [player] the page's `data-player`: `psp` (play-psp.html), `dos` (play-dos.html), `3ds`
 * (play-3ds.html) or undefined
 * (play.html); each isolated page plays its own core only, and play.html none of them
 * @returns {PlayParams | null} null when any value is missing or invalid, or the core doesn't belong on this page
 */
export function parsePlayParams(search, player) {
	const query = new URLSearchParams(search);
	const game = query.get('game') ?? '';
	const rom = query.get('rom') ?? '';
	const core = query.get('core') ?? '';
	const name = query.get('name') ?? '';
	if (!guid.test(game) || !/^[1-9][0-9]{0,15}$/.test(rom) || !cores.includes(core)) return null;
	if (name.length < 1 || name.length > 255) return null;
	if ((Object.hasOwn(isolatedPlayers, core) ? isolatedPlayers[core] : undefined) !== player) return null;
	// Sega CD B6: `disc` is absent or exactly `1`, given once.
	const disc = query.getAll('disc');
	if (disc.length > 1 || (disc.length === 1 && disc[0] !== '1')) return null;
	// Saves B17: both or neither, each once; the id is a lowercase 32-hex GUID.
	const save = query.getAll('save');
	const saveKind = query.getAll('saveKind');
	if (save.length > 1 || saveKind.length > 1 || save.length !== saveKind.length) return null;
	if (save.length === 1 && (!/^[0-9a-f]{32}$/.test(save[0]) || !['state', 'battery'].includes(saveKind[0])))
		return null;
	// Multi-disc M5 (Saturn and Sega CD S5): `set` is absent or exactly `1`, given once, for PCSX ReARMed, Yabause, or
	// Genesis Plus GX with `disc=1` (it also plays cartridges). Amiga F6: PUAE too, with `disc=1`.
	const set = query.getAll('set');
	const setCore =
		core === 'pcsx_rearmed' ||
		core === 'yabause' ||
		((core === 'genesis_plus_gx' || core === 'puae') && disc.length === 1);
	if (set.length > 1 || (set.length === 1 && (set[0] !== '1' || !setCore))) return null;
	return {
		game,
		rom: Number(rom),
		core,
		name,
		disc: disc.length === 1,
		set: set.length === 1,
		save: save.length === 1 ? { id: save[0], kind: /** @type {'state' | 'battery'} */ (saveKind[0]) } : null
	};
}

/**
 * The BIOS file a core needs from `Paths:Bios` (served by `/api/emulator/bios/<name>`), or null when it needs none.
 * Gearcoleco looks for the file by this exact name next to the ROM. FCEUmm needs `disksys.rom` for a Famicom Disk
 * System release (named `.fds`, any case) and nothing for an NES cartridge. Snes9x needs `STBIOS.bin` for a Sufami Turbo
 * release (`.st`) and `BS-X.bin` for a Satellaview one (`.bs`), any case, and nothing for a SNES cartridge; it looks
 * for them next to the ROM first.
 * @param {string} core
 * @param {string} name the release name
 * @returns {string | null}
 */
export function biosFor(core, name) {
	if (core === 'gearcoleco') return 'colecovision.rom';
	if (core === 'snes9x' && /\.st$/i.test(name)) return 'STBIOS.bin';
	if (core === 'snes9x' && /\.bs$/i.test(name)) return 'BS-X.bin';
	return core === 'fceumm' && /\.fds$/i.test(name) ? 'disksys.rom' : null;
}

/**
 * The play page's message for a BIOS check's HTTP status, or null when it succeeded (2xx).
 * @param {string} name the BIOS file name
 * @param {number} status
 * @returns {string | null}
 */
export function biosError(name, status) {
	if (status >= 200 && status < 300) return null;
	if (status === 401) return 'Your session has expired. Sign in again.';
	if (status === 404)
		return `This system needs its BIOS: ask an Admin to put ${name} in the BIOS folder (Paths:Bios).`;
	return `Couldn't load the BIOS (HTTP ${status}).`;
}

/**
 * EmulatorJS 4.2.3 takes the controls from the core's group, `segaMS` for smsplus (no Start button). A `.gg` release
 * gets the Game Gear scheme instead. Genesis B5: Genesis Plus GX's first group is also `segaMS`, so it gets the Mega
 * Drive scheme explicitly. 32X B3: PicoDrive's first group is `segaMS` too, so it gets the 32X scheme. Saturn B6:
 * Yabause gets `segaSaturn`, upstream's default too, set so it doesn't depend on the group order. Anything else keeps
 * the default (null).
 * @param {string} core
 * @param {string} name the release's file name
 * @returns {string | null}
 */
export function controlSchemeFor(core, name) {
	if (core === 'genesis_plus_gx') return 'segaMD';
	if (core === 'picodrive') return 'sega32x';
	if (core === 'yabause') return 'segaSaturn';
	return core === 'smsplus' && name.toLowerCase().endsWith('.gg') ? 'segaGG' : null;
}

/**
 * B3/B8 (SNES B3 adds Snes9x, 3DO B6 Opera, C64 B7 VICE, PET VICE xpet, VIC-20 VICE xvic, N64 Mupen64Plus-Next): the cores whose files the operator supplies, served by the API after a length and hash check, with the
 * wording of their messages: the system label, the core's display name, the short name of the fallback message and
 * the option holding the folder.
 * @type {Record<string, { system: string, display: string, short: string, option: string }>}
 */
const operatorCores = {
	genesis_plus_gx: {
		system: 'Genesis and Sega CD games',
		display: 'Genesis Plus GX',
		short: 'Genesis',
		option: 'Emulator:GenesisCorePath'
	},
	picodrive: {
		system: '32X games',
		display: 'PicoDrive',
		short: 'PicoDrive',
		option: 'Emulator:PicodriveCorePath'
	},
	snes9x: {
		system: 'SNES games',
		display: 'Snes9x',
		short: 'Snes9x',
		option: 'Emulator:Snes9xCorePath'
	},
	opera: {
		system: '3DO games',
		display: 'Opera',
		short: 'Opera',
		option: 'Emulator:OperaCorePath'
	},
	vice_x64sc: {
		system: 'Commodore 64 games',
		display: 'VICE',
		short: 'VICE',
		option: 'Emulator:ViceCorePath'
	},
	vice_xpet: {
		system: 'Commodore PET games',
		display: 'VICE (PET)',
		short: 'VICE',
		option: 'Emulator:ViceCorePath'
	},
	vice_xvic: {
		system: 'Commodore VIC-20 games',
		display: 'VICE (VIC-20)',
		short: 'VICE',
		option: 'Emulator:ViceCorePath'
	},
	mupen64plus_next: {
		system: 'Nintendo 64 games',
		display: 'Mupen64Plus-Next',
		short: 'Mupen64Plus',
		option: 'Emulator:Mupen64PlusCorePath'
	}
};

/**
 * @param {string} core
 * @returns {boolean} whether the operator supplies this core's files
 */
export function isOperatorCore(core) {
	return Object.hasOwn(operatorCores, core);
}

/**
 * B2: the bodyless availability check run before the ROM is fetched.
 * @param {string} core
 * @returns {string}
 */
export function operatorCoreCheckUrl(core) {
	return `/api/emulator/cores/${core}`;
}

/**
 * B3/B8: EJS_paths, mapping the files EmulatorJS asks for by name to the authenticated same-origin endpoint.
 * @param {string} core
 * @returns {Record<string, string>}
 */
export function operatorCorePaths(core) {
	return Object.fromEntries(
		[`${core}-wasm.data`, `${core}-legacy-wasm.data`, `${core}.json`].map((f) => [
			f,
			`${operatorCoreCheckUrl(core)}/${f}`
		])
	);
}

/**
 * B3/B8: the play page's message for the core availability check, or null when the files are there and match.
 * @param {string} core an operator-supplied core
 * @param {number | null} status the HTTP status, or null for a network failure
 * @returns {string | null}
 */
export function operatorCoreError(core, status) {
	if (status !== null && status >= 200 && status < 300) return null;
	if (status === 401) return sessionExpired;
	const { system, display, short, option } = operatorCores[core];
	if (status === 404) return `${system} need the ${display} core files: ask an Admin to set them up (${option}).`;
	if (status === 409)
		return `The ${display} core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (${option}).`;
	return status === null
		? `Couldn't load the ${short} core.`
		: `Couldn't load the ${short} core (HTTP ${status}).`;
}

/**
 * Cores pinned by the vendored getCore() edit: the emulator's core chooser is hidden for them (other settings stay).
 * @param {string} core
 * @returns {boolean}
 */
export function hidesCoreChooser(core) {
	return [
		'smsplus',
		'fceumm',
		'pcsx_rearmed',
		'genesis_plus_gx',
		'picodrive',
		'yabause',
		'snes9x',
		'desmume',
		'opera',
		'vice_x64sc',
		'vice_xpet',
		'vice_xvic',
		'mupen64plus_next'
	].includes(core);
}

/** PCSX ReARMed's optional firmware: the operator's scph5501.bin, else the core's built-in HLE BIOS. */
export const psxBios = 'scph5501.bin';

/** The exact size of scph5501.bin (512 KiB). */
export const psxBiosBytes = 512 * 1024;

export const sessionExpired = 'Your session has expired. Sign in again.';

/**
 * The play page's message for an optional BIOS request (PlayStation, Saturn): null when it may go on (2xx, or 404 for
 * HLE).
 * @param {number | null} status the HTTP status, or null for a network or body-read failure
 * @returns {string | null}
 */
export function optionalBiosError(status) {
	if (status !== null && ((status >= 200 && status < 300) || status === 404)) return null;
	if (status === 401) return sessionExpired;
	return status === null ? "Couldn't load the BIOS." : `Couldn't load the BIOS (HTTP ${status}).`;
}

/** Shown when the BIOS folder's scph5501.bin isn't exactly 512 KiB. */
export const psxBiosSizeError = `The PlayStation BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB ${psxBios}: ask an Admin to replace it.`;

/** Saturn B7: Yabause's optional firmware, the operator's saturn_bios.bin, else the core's built-in HLE BIOS. */
export const saturnBios = 'saturn_bios.bin';

/** The exact size of saturn_bios.bin (512 KiB). */
export const saturnBiosBytes = 512 * 1024;

/** Saturn B7: shown when the BIOS folder's saturn_bios.bin isn't exactly 512 KiB. */
/** Saturn and Sega CD S6: Yabause aborts a playlist without a real BIOS, so a Saturn disc set needs one. */
export const saturnSetBiosMissing =
	'Playing all Saturn discs needs a BIOS: ask an Admin to put saturn_bios.bin in the BIOS folder (Paths:Bios).';

export const saturnBiosSizeError =`The Saturn BIOS in the BIOS folder (Paths:Bios) must be the 512 KiB ${saturnBios}: ask an Admin to replace it.`;

/** Saturn B7: every name Yabause looks for its firmware under, in the content root. */
const saturnBiosNames = [saturnBios, 'sega_101.bin', 'mpr-17933.bin'];

/**
 * Sega CD B7: the regional BIOS files Genesis Plus GX looks for in the content root, fetched in this order (U, E, J).
 * At least one is needed; every one found is handed over.
 */
export const segaCdBios = ['bios_CD_U.bin', 'bios_CD_E.bin', 'bios_CD_J.bin'];

/** The exact size of each Sega CD BIOS file (128 KiB). */
export const segaCdBiosBytes = 131072;

/**
 * Sega CD B7: shown when a Sega CD BIOS file in the BIOS folder isn't exactly 128 KiB.
 * @param {string} name the BIOS file name
 * @returns {string}
 */
export function segaCdBiosSizeError(name) {
	return `The Sega CD BIOS ${name} in the BIOS folder (Paths:Bios) must be 128 KiB: ask an Admin to replace it.`;
}

/** Sega CD B7: shown when none of the three Sega CD BIOS files is in the BIOS folder. */
export const segaCdBiosMissing =
	'Sega CD games need a BIOS: ask an Admin to put bios_CD_U.bin, bios_CD_E.bin or bios_CD_J.bin in the BIOS folder (Paths:Bios).';

const segaCdBiosNames = segaCdBios.map((name) => name.toLowerCase());

/**
 * 3DO B8: the 3DO BIOS files, fetched in this order. At least one is needed; every one found is handed over, and Opera
 * boots the first present in its own order.
 */
export const threeDoBios = ['panafz10.bin', 'panafz1.bin', 'panafz1j.bin', 'goldstar.bin', 'sanyotry.bin'];

/** The exact size of each 3DO BIOS file (1 MiB). */
export const threeDoBiosBytes = 1048576;

/**
 * 3DO B8: shown when a 3DO BIOS file in the BIOS folder isn't exactly 1 MiB.
 * @param {string} name the BIOS file name
 * @returns {string}
 */
export function threeDoBiosSizeError(name) {
	return `The 3DO BIOS ${name} in the BIOS folder (Paths:Bios) must be 1 MiB: ask an Admin to replace it.`;
}

/** 3DO B8: shown when none of the five 3DO BIOS files is in the BIOS folder. */
export const threeDoBiosMissing =
	'3DO games need a BIOS: ask an Admin to put panafz10.bin, panafz1.bin, panafz1j.bin, goldstar.bin or sanyotry.bin in the BIOS folder (Paths:Bios).';

const threeDoBiosNames = threeDoBios.map((name) => name.toLowerCase());

/**
 * TurboGrafx-CD B7: the System Cards looked for, one at a time in this order; the first one found is the only one used,
 * handed to Beetle PCE as `syscard3.pce` (the file its default `pce_cdbios` value opens).
 */
export const turboGrafxCdCards = ['syscard3.pce', 'syscard3u.pce', 'syscard2.pce', 'syscard2u.pce', 'syscard1.pce'];

/** TurboGrafx-CD B7: the Games Express card, optional; Beetle PCE opens it by itself for a Games Express disc. */
export const turboGrafxCdGamesExpress = 'gexpress.pce';

/** TurboGrafx-CD B8: the largest BIOS file accepted (1 MiB); Beetle PCE takes any size and strips a copier header. */
export const turboGrafxCdBiosMaxBytes = 1048576;

/**
 * TurboGrafx-CD B7: shown when a TurboGrafx-CD BIOS file in the BIOS folder is empty or over 1 MiB.
 * @param {string} name the BIOS file name
 * @returns {string}
 */
export function turboGrafxCdBiosSizeError(name) {
	return `The TurboGrafx-CD BIOS ${name} in the BIOS folder (Paths:Bios) is empty or larger than 1 MiB: ask an Admin to replace it.`;
}

/** TurboGrafx-CD B7: shown when no System Card is in the BIOS folder. */
export const turboGrafxCdBiosMissing =
	'TurboGrafx-CD games need a System Card BIOS: ask an Admin to put syscard3.pce in the BIOS folder (Paths:Bios).';

/** TurboGrafx-CD B9: every BIOS name the player may write to the content root. */
export const turboGrafxCdBiosNames = [...turboGrafxCdCards, turboGrafxCdGamesExpress];

/**
 * The play page's message for a failed play-disc request.
 * @param {number | null} status the HTTP status, or null for a network or body-read failure
 * @param {string | null} code the problem's `code`, when the body had one
 * @param {string} [core] PSP B3: `ppsspp` gets the UMD wording
 * @param {boolean} [set] Multi-disc M6: a PlayStation disc set (play-disc-set) gets its own too-large wording
 * @returns {string}
 */
export function discError(status, code, core = 'pcsx_rearmed', set = false) {
	const psp = core === 'ppsspp';
	if (status === 401) return sessionExpired;
	// Amiga F7: a PUAE floppy set (play-disc-set) has disk wording for every other outcome.
	if (core === 'puae') {
		if (code === 'InvalidPlayDisc') return "These disks can't be played in the browser: one of them is empty.";
		return status === null ? "Couldn't load the disks." : `Couldn't load the disks (HTTP ${status}).`;
	}
	if (code === 'PlayDiscTooLarge' && set)
		return 'This disc set is larger than 2 GiB, too large to play in the browser. CHD images are much smaller.';
	if (code === 'PlayDiscTooLarge')
		return psp
			? 'This UMD image is larger than 2 GiB, too large to play in the browser.'
			: 'This disc is larger than 2 GiB, too large to play in the browser.';
	if (code === 'InvalidPlayDisc')
		return psp
			? "This UMD image can't be played in the browser: it must be one non-empty .iso, .cso, .pbp or .chd whose name matches the release."
			: "This disc can't be played in the browser: its cue sheet or files are invalid or don't match.";
	return status === null ? "Couldn't load the disc." : `Couldn't load the disc (HTTP ${status}).`;
}

/** @typedef {{ secure: boolean, isolated: boolean, sharedArrayBuffer: boolean, webgl2: boolean }} IsolatedBrowser */

/**
 * PSP B3, MS-DOS P6, 3DS G3: why this browser context can't run an isolated page's core, checked before the game is fetched,
 * or null when it can. DOSBox Pure's legacy payload needs no WebGL 2.
 * @param {IsolatedBrowser} browser
 * @param {string} core `ppsspp`, `dosbox_pure` or `azahar`
 * @returns {string | null}
 */
export function isolatedBrowserError(browser, core) {
	if (core === 'dosbox_pure') {
		if (!browser.secure)
			return "DOS games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them.";
		if (!browser.isolated || !browser.sharedArrayBuffer)
			return "This page isn't cross-origin isolated, so DOS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers.";
		return null;
	}
	if (core === 'azahar') {
		if (!browser.secure)
			return "3DS games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them.";
		if (!browser.isolated || !browser.sharedArrayBuffer)
			return "This page isn't cross-origin isolated, so 3DS games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers.";
		if (!browser.webgl2) return "3DS games need WebGL 2, which this browser or graphics driver doesn't provide.";
		return null;
	}
	if (!browser.secure)
		return "PSP games need a secure page: open RnRomManager over HTTPS or on localhost. A plain-HTTP network address can't run them.";
	if (!browser.isolated || !browser.sharedArrayBuffer)
		return "This page isn't cross-origin isolated, so PSP games can't use threads. Ask an Admin to check that the proxy keeps the Cross-Origin-Opener-Policy and Cross-Origin-Embedder-Policy headers.";
	if (!browser.webgl2) return "PSP games need WebGL 2, which this browser or graphics driver doesn't provide.";
	return null;
}

/**
 * The name of the File EmulatorJS writes the content under: the release name, with '#' replaced (EmulatorJS cuts
 * names there). A standalone PlayStation image named like the BIOS gets a name scoped to the game, so the firmware
 * written at /scph5501.bin can't overwrite it. Saturn B7: the same for a Saturn image named like any of Yabause's
 * three BIOS names, so the firmware can't overwrite it and Yabause never takes the disc for its BIOS. Sega CD B7: the
 * same for any Genesis Plus GX content named like a Sega CD BIOS, disc or cartridge alike (harmless for a cartridge).
 * 3DO B8: the same for an Opera disc named like any of the five 3DO BIOS files. TurboGrafx-CD B9: the same for any
 * Beetle PCE content named like a System Card or the Games Express card, disc or HuCard alike. MS-DOS P8: every DOSBox
 * Pure release is scoped to its game, since DOSBox Pure keeps its browser save under the content name and two games
 * may share a release name.
 * @param {string} core
 * @param {string} game the game id
 * @param {string} name the release's file name
 * @returns {string}
 */
export function contentFileName(core, game, name) {
	const safe = name.replaceAll('#', '_');
	const lower = name.toLowerCase();
	const clashes =
		(core === 'pcsx_rearmed' && lower === psxBios) ||
		(core === 'yabause' && saturnBiosNames.includes(lower)) ||
		(core === 'genesis_plus_gx' && segaCdBiosNames.includes(lower)) ||
		(core === 'opera' && threeDoBiosNames.includes(lower)) ||
		(core === 'mednafen_pce' && turboGrafxCdBiosNames.includes(lower));
	return clashes || core === 'dosbox_pure' ? `${game}-${safe}` : safe;
}

const sizeNumber = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1, minimumFractionDigits: 1 });

/**
 * U4: a size for the loading label, `512 B`, `1.5 KiB`, `16.0 MiB`, `8.0 GiB`. A copy of `formatSize` in
 * `src/lib/format.ts` (the static play page can't import from `src/lib`): keep the two in step.
 * @param {number} bytes
 * @returns {string}
 */
export function contentSize(bytes) {
	if (bytes < 1024) return `${bytes} B`;
	if (bytes < 1024 * 1024) return `${sizeNumber.format(bytes / 1024)} KiB`;
	if (bytes < 1024 * 1024 * 1024) return `${sizeNumber.format(bytes / (1024 * 1024))} MiB`;
	return `${sizeNumber.format(bytes / (1024 * 1024 * 1024))} GiB`;
}
