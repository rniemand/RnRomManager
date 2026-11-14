// Browser emulators B5: fetches the release through the download endpoint and starts EmulatorJS on it.
import { biosError, biosFor, controlSchemeFor, parsePlayParams } from './params.js';

const status = /** @type {HTMLElement} */ (document.getElementById('status'));

/** @param {string} text */
function show(text) {
	status.textContent = text;
}

/**
 * Checks a core's BIOS before the ROM is fetched (spec B5). Only the status matters: the unused body is cancelled,
 * and EmulatorJS downloads the file itself from the same URL.
 * @param {string} name
 * @returns {Promise<string | null>} the message to show, or null when the BIOS is there
 */
async function checkBios(name) {
	let response;
	try {
		response = await fetch(`/api/emulator/bios/${name}`, { credentials: 'same-origin' });
	} catch {
		return "Couldn't load the BIOS.";
	}
	try {
		await response.body?.cancel();
	} catch {
		// A failed cancel changes nothing: the status has already arrived.
	}
	return biosError(name, response.status);
}

async function start() {
	const params = parsePlayParams(location.search);
	if (!params) return show('Invalid emulator link.');

	show('Loading…');
	const bios = biosFor(params.core);
	if (bios) {
		const problem = await checkBios(bios);
		if (problem) return show(problem);
	}

	const response = await fetch(`/api/games/${params.game}/roms/${params.rom}/download`, {
		credentials: 'same-origin'
	});
	if (!response.ok)
		return show(
			response.status === 401
				? 'Your session has expired. Sign in again.'
				: `Couldn't load the ROM (HTTP ${response.status}).`
		);
	// EmulatorJS cuts the core's file name at the first '#', which would drop the extension.
	const file = new File([await response.blob()], params.name.replaceAll('#', '_'));

	// Module code is strict: the globals loader.js reads must be set on window.
	const ejs = /** @type {Record<string, unknown>} */ (/** @type {unknown} */ (window));
	ejs.EJS_player = '#game';
	ejs.EJS_core = params.core;
	ejs.EJS_gameUrl = file;
	ejs.EJS_gameName = params.name;
	ejs.EJS_pathtodata = '/emulator/vendor/data/';
	// Keep true: with databases off EmulatorJS skips its cache and always GETs the BIOS (its HEAD gets a 404).
	ejs.EJS_disableDatabases = true;
	ejs.EJS_disableAutoLang = false;
	// EmulatorJS writes the BIOS next to the ROM under the URL's last segment, the name the core looks for.
	if (bios) ejs.EJS_biosUrl = `/api/emulator/bios/${bios}`;
	// Game Gear and Master System stay on SMS Plus GX (GPL): hide only the core selector.
	if (params.core === 'smsplus') ejs.EJS_hideSettings = ['retroarch_core'];
	const scheme = controlSchemeFor(params.core, params.name);
	if (scheme) ejs.EJS_controlScheme = scheme;
	ejs.EJS_Buttons = {
		saveState: false,
		loadState: false,
		quickSave: false,
		quickLoad: false,
		netplay: false,
		cheat: false,
		exitEmulation: false
	};
	ejs.EJS_defaultOptions = { 'save-save-interval': '30' };

	const loader = document.createElement('script');
	loader.src = '/emulator/vendor/data/loader.js';
	loader.addEventListener('error', () => show("Couldn't load the emulator."));
	loader.addEventListener('load', () => show(''));
	document.body.append(loader);
}

/** Settles once the page has started the loader or shown why it couldn't (tests await it). */
export const started = start().catch(() => show("Couldn't load the ROM."));
