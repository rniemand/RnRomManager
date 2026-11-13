// Browser emulators B5: fetches the release through the download endpoint and starts EmulatorJS on it.
import { parsePlayParams } from './params.js';

const status = /** @type {HTMLElement} */ (document.getElementById('status'));

/** @param {string} text */
function show(text) {
	status.textContent = text;
}

async function start() {
	const params = parsePlayParams(location.search);
	if (!params) return show('Invalid emulator link.');

	show('Loading…');
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
	ejs.EJS_disableDatabases = true;
	ejs.EJS_disableAutoLang = false;
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

start().catch(() => show("Couldn't load the ROM."));
