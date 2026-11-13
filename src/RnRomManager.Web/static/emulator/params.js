// Browser emulators B5 step 1: the play page's query, validated before anything is fetched or loaded.

/** @typedef {{ game: string, rom: number, core: string, name: string }} PlayParams */

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const cores = ['gambatte', 'mgba', 'handy', 'virtualjaguar'];

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
