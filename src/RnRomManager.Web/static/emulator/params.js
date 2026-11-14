// Browser emulators B5 step 1: the play page's query, validated before anything is fetched or loaded.

/** @typedef {{ game: string, rom: number, core: string, name: string }} PlayParams */

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
	'genesis_plus_gx'
];

/** PSP B3: the core that runs only on the isolated play-psp.html page (and never in the modal's play.html). */
export const pspCore = 'ppsspp';

/**
 * @param {string} search `location.search`
 * @param {boolean} [pspPage] PSP B3: true on play-psp.html, which plays PPSSPP only; play.html rejects PPSSPP
 * @returns {PlayParams | null} null when any value is missing or invalid, or the core doesn't belong on this page
 */
export function parsePlayParams(search, pspPage = false) {
	const query = new URLSearchParams(search);
	const game = query.get('game') ?? '';
	const rom = query.get('rom') ?? '';
	const core = query.get('core') ?? '';
	const name = query.get('name') ?? '';
	if (!guid.test(game) || !/^[1-9][0-9]{0,15}$/.test(rom) || !cores.includes(core)) return null;
	if (name.length < 1 || name.length > 255) return null;
	if ((core === pspCore) !== pspPage) return null;
	return { game, rom: Number(rom), core, name };
}

/**
 * The BIOS file a core needs from `Paths:Bios` (served by `/api/emulator/bios/<name>`), or null when it needs none.
 * Gearcoleco looks for the file by this exact name next to the ROM.
 * @param {string} core
 * @returns {string | null}
 */
export function biosFor(core) {
	return core === 'gearcoleco' ? 'colecovision.rom' : null;
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
 * Drive scheme explicitly. Anything else keeps the default (null).
 * @param {string} core
 * @param {string} name the release's file name
 * @returns {string | null}
 */
export function controlSchemeFor(core, name) {
	if (core === genesisCore) return 'segaMD';
	return core === 'smsplus' && name.toLowerCase().endsWith('.gg') ? 'segaGG' : null;
}

/** Genesis B5: the core whose files the operator supplies (Emulator:GenesisCorePath), served by the API. */
export const genesisCore = 'genesis_plus_gx';

/** Genesis B4: the availability check run before the ROM is fetched. */
export const genesisCheckUrl = '/api/emulator/genesis';

/**
 * Genesis B5: EJS_paths, mapping the files EmulatorJS asks for by name to the authenticated same-origin endpoint.
 * @returns {Record<string, string>}
 */
export function genesisPaths() {
	return Object.fromEntries(
		['genesis_plus_gx-wasm.data', 'genesis_plus_gx-legacy-wasm.data', 'genesis_plus_gx.json'].map((f) => [
			f,
			`${genesisCheckUrl}/${f}`
		])
	);
}

/**
 * Genesis B4: the play page's message for the core availability check, or null when the files are there and match.
 * @param {number | null} status the HTTP status, or null for a network failure
 * @returns {string | null}
 */
export function genesisCoreError(status) {
	if (status !== null && status >= 200 && status < 300) return null;
	if (status === 401) return sessionExpired;
	if (status === 404)
		return 'Genesis games need the Genesis Plus GX core files: ask an Admin to set them up (Emulator:GenesisCorePath).';
	if (status === 409)
		return "The Genesis Plus GX core files don't match EmulatorJS 4.2.3: ask an Admin to replace them (Emulator:GenesisCorePath).";
	return status === null
		? "Couldn't load the Genesis core."
		: `Couldn't load the Genesis core (HTTP ${status}).`;
}

/**
 * Cores pinned by the vendored getCore() edit: the emulator's core chooser is hidden for them (other settings stay).
 * @param {string} core
 * @returns {boolean}
 */
export function hidesCoreChooser(core) {
	return ['smsplus', 'fceumm', 'pcsx_rearmed', genesisCore].includes(core);
}

/** PCSX ReARMed's optional firmware: the operator's scph5501.bin, else the core's built-in HLE BIOS. */
export const psxBios = 'scph5501.bin';

/** The exact size of scph5501.bin (512 KiB). */
export const psxBiosBytes = 512 * 1024;

export const sessionExpired = 'Your session has expired. Sign in again.';

/**
 * The play page's message for the optional PlayStation BIOS request: null when it may go on (2xx, or 404 for HLE).
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

/**
 * The play page's message for a failed play-disc request.
 * @param {number | null} status the HTTP status, or null for a network or body-read failure
 * @param {string | null} code the problem's `code`, when the body had one
 * @param {string} [core] PSP B3: `ppsspp` gets the UMD wording and its 2 GiB limit
 * @returns {string}
 */
export function discError(status, code, core = 'pcsx_rearmed') {
	const psp = core === pspCore;
	if (status === 401) return sessionExpired;
	if (code === 'PlayDiscTooLarge')
		return psp
			? 'This UMD image is larger than 2 GiB, too large to play in the browser.'
			: 'This disc is larger than 1 GiB, too large to play in the browser.';
	if (code === 'InvalidPlayDisc')
		return psp
			? "This UMD image can't be played in the browser: it must be one non-empty .iso whose name matches the release."
			: "This disc can't be played in the browser: its cue sheet or files are invalid or don't match.";
	return status === null ? "Couldn't load the disc." : `Couldn't load the disc (HTTP ${status}).`;
}

/** @typedef {{ secure: boolean, isolated: boolean, sharedArrayBuffer: boolean, webgl2: boolean }} PspBrowser */

/**
 * PSP B3: why this browser context can't run PPSSPP, checked before the ISO is fetched, or null when it can.
 * @param {PspBrowser} browser
 * @returns {string | null}
 */
export function pspBrowserError(browser) {
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
 * written at /scph5501.bin can't overwrite it.
 * @param {string} core
 * @param {string} game the game id
 * @param {string} name the release's file name
 * @returns {string}
 */
export function contentFileName(core, game, name) {
	const safe = name.replaceAll('#', '_');
	return core === 'pcsx_rearmed' && name.toLowerCase() === psxBios ? `${game}-${safe}` : safe;
}