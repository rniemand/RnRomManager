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
	'smsplus'
];

/**
 * @param {string} search `location.search`
 * @returns {PlayParams | null} null when any value is missing or invalid
 */
export function parsePlayParams(search) {
	const query = new URLSearchParams(search);
	const game = query.get('game') ?? '';
	const rom = query.get('rom') ?? '';
	const core = query.get('core') ?? '';
	const name = query.get('name') ?? '';
	if (!guid.test(game) || !/^[1-9][0-9]{0,15}$/.test(rom) || !cores.includes(core)) return null;
	if (name.length < 1 || name.length > 255) return null;
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
 * gets the Game Gear scheme instead; anything else keeps the default (null).
 * @param {string} core
 * @param {string} name the release's file name
 * @returns {string | null}
 */
export function controlSchemeFor(core, name) {
	return core === 'smsplus' && name.toLowerCase().endsWith('.gg') ? 'segaGG' : null;
}
