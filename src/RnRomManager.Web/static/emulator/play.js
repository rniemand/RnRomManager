// Browser emulators B5: fetches the release through the download endpoint and starts EmulatorJS on it.
// Play B4: PlayStation fetches its disc from play-disc, with the optional scph5501.bin handed over in memory.
// PSP B3: on play-psp.html (its own cross-origin isolated tab) PPSSPP checks the browser, then fetches its UMD image
// from play-disc and runs threaded.
import {
	biosError,
	biosFor,
	contentFileName,
	controlSchemeFor,
	discError,
	hidesCoreChooser,
	optionalBiosError,
	parsePlayParams,
	pspBrowserError,
	pspCore,
	psxBios,
	psxBiosBytes,
	psxBiosSizeError,
	sessionExpired
} from './params.js';

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

/**
 * Play B4: one GET of the optional scph5501.bin, read with a 512 KiB bound (cancelled as soon as it's exceeded).
 * @returns {Promise<{ bios: ArrayBuffer | null } | { error: string }>} the firmware, null for HLE (404), or why to stop
 */
async function loadPsxBios() {
	let response;
	try {
		response = await fetch(`/api/emulator/bios/${psxBios}`, { credentials: 'same-origin' });
	} catch {
		return { error: /** @type {string} */ (optionalBiosError(null)) };
	}
	const problem = optionalBiosError(response.status);
	if (problem || response.status === 404) {
		try {
			await response.body?.cancel();
		} catch {
			// The status is all that's needed.
		}
		return problem ? { error: problem } : { bios: null };
	}
	if (!response.body) return { error: /** @type {string} */ (optionalBiosError(null)) };

	const bytes = new Uint8Array(psxBiosBytes);
	let length = 0;
	const reader = response.body.getReader();
	try {
		for (;;) {
			const { done, value } = await reader.read();
			if (done) break;
			if (length + value.length > psxBiosBytes) {
				await reader.cancel().catch(() => {});
				return { error: psxBiosSizeError };
			}
			bytes.set(value, length);
			length += value.length;
		}
	} catch {
		return { error: /** @type {string} */ (optionalBiosError(null)) };
	}
	return length === psxBiosBytes ? { bios: bytes.buffer } : { error: psxBiosSizeError };
}

/**
 * PSP B3: what PPSSPP needs from this browser context, read before anything is fetched.
 * @returns {import('./params.js').PspBrowser}
 */
function pspBrowser() {
	let webgl2 = false;
	try {
		webgl2 = !!document.createElement('canvas').getContext('webgl2');
	} catch {
		// No WebGL 2 context: reported below.
	}
	return {
		secure: window.isSecureContext === true,
		isolated: window.crossOriginIsolated === true,
		sharedArrayBuffer: typeof SharedArrayBuffer === 'function',
		webgl2
	};
}

/**
 * Play B4: the disc from play-disc (a standalone image, or a ZIP of the playback cue and its tracks; PSP B3: a UMD
 * image).
 * @param {string} url
 * @param {string} core
 * @returns {Promise<{ data: Blob } | { error: string }>}
 */
async function loadDisc(url, core) {
	let response;
	try {
		response = await fetch(url, { credentials: 'same-origin' });
	} catch {
		return { error: discError(null, null, core) };
	}
	if (!response.ok) {
		let code = null;
		try {
			const body = await response.json();
			code = typeof body?.code === 'string' ? body.code : null;
		} catch {
			// No problem body: the status decides.
		}
		return { error: discError(response.status, code, core) };
	}
	try {
		return { data: await response.blob() };
	} catch {
		return { error: discError(null, null, core) };
	}
}

async function start() {
	const pspPage = document.body.dataset.player === 'psp';
	const params = parsePlayParams(location.search, pspPage);
	if (!params) return show('Invalid emulator link.');

	const isPsx = params.core === 'pcsx_rearmed';
	const isPsp = params.core === pspCore;
	if (isPsp) {
		const problem = pspBrowserError(pspBrowser());
		if (problem) return show(problem);
	}
	show('Loading…');
	const bios = biosFor(params.core);
	if (bios) {
		const problem = await checkBios(bios);
		if (problem) return show(problem);
	}
	/** @type {ArrayBuffer | null} */
	let psxFirmware = null;
	if (isPsx) {
		const result = await loadPsxBios();
		if ('error' in result) return show(result.error);
		psxFirmware = result.bios;
	}

	/** @type {Blob} */
	let content;
	if (isPsx || isPsp) {
		const disc = await loadDisc(`/api/games/${params.game}/roms/${params.rom}/play-disc`, params.core);
		if ('error' in disc) return show(disc.error);
		content = disc.data;
	} else {
		const response = await fetch(`/api/games/${params.game}/roms/${params.rom}/download`, {
			credentials: 'same-origin'
		});
		if (!response.ok)
			return show(
				response.status === 401 ? sessionExpired : `Couldn't load the ROM (HTTP ${response.status}).`
			);
		content = await response.blob();
	}
	const file = new File([content], contentFileName(params.core, params.game, params.name));

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
	// PCSX ReARMed finds scph5501.bin in the content root; GameManager writes this key before loading the content.
	if (psxFirmware) ejs.EJS_externalFiles = { [`/${psxBios}`]: psxFirmware };
	// Pinned cores (SMS Plus GX, FCEUmm, PCSX ReARMed): hide only the core selector.
	if (hidesCoreChooser(params.core)) ejs.EJS_hideSettings = ['retroarch_core'];
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
	// PSP B3: PPSSPP has only a threaded build; the vendored loader also ignores a saved "threads disabled".
	if (isPsp) ejs.EJS_threads = true;

	const loader = document.createElement('script');
	loader.src = '/emulator/vendor/data/loader.js';
	loader.addEventListener('error', () => show("Couldn't load the emulator."));
	loader.addEventListener('load', () => show(''));
	document.body.append(loader);
}

/** Settles once the page has started the loader or shown why it couldn't (tests await it). */
export const started = start().catch(() => show("Couldn't load the ROM."));
