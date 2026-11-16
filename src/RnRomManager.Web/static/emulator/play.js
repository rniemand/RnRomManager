// Browser emulators B5: fetches the release through the download endpoint and starts EmulatorJS on it.
// Play B4: PlayStation fetches its disc from play-disc, with the optional scph5501.bin handed over in memory.
// PSP B3: on play-psp.html (its own cross-origin isolated tab) PPSSPP checks the browser, then fetches its UMD image
// from play-disc and runs threaded. Genesis B4/B5: an operator-supplied core (Genesis Plus GX) checks its files
// first and loads them from the API. Saturn B6/B7: Yabause fetches its disc from play-disc, with the optional
// saturn_bios.bin handed over in memory like scph5501.bin. Sega CD B7: Genesis Plus GX with `disc=1` needs at least one of
// the regional Sega CD BIOS files, fetched one after another and all handed over in memory, then loads from play-disc.
// 3DO B8: Opera does the same with the five 3DO BIOS files, and always loads from play-disc. 3DO B11: Opera writes its
// NVRAM only on reset, so the modal asks the page to reset and sync /data/saves before it closes. Saves B17/B18: a
// library save is fetched before the loader and applied once at game start; the modal asks the page to export the
// running game's state or battery save, which it sends back for upload.
import {
	biosError,
	biosFor,
	contentFileName,
	controlSchemeFor,
	discError,
	isOperatorCore,
	operatorCoreCheckUrl,
	operatorCoreError,
	operatorCorePaths,
	hidesCoreChooser,
	optionalBiosError,
	parsePlayParams,
	pspBrowserError,
	pspCore,
	psxBios,
	psxBiosBytes,
	psxBiosSizeError,
	saturnBios,
	saturnBiosBytes,
	saturnBiosSizeError,
	segaCdBios,
	segaCdBiosBytes,
	segaCdBiosMissing,
	segaCdBiosSizeError,
	sessionExpired,
	threeDoBios,
	threeDoBiosBytes,
	threeDoBiosMissing,
	threeDoBiosSizeError
} from './params.js';

const status = /** @type {HTMLElement} */ (document.getElementById('status'));
const progress = /** @type {HTMLProgressElement} */ (document.getElementById('loading'));
const downloaded = /** @type {HTMLElement} */ (document.getElementById('downloaded'));
const formatBytes = new Intl.NumberFormat(undefined, { style: 'unit', unit: 'byte', unitDisplay: 'long' });
const formatPercent = new Intl.NumberFormat(undefined, { style: 'percent' });

/** @param {string} text */
function show(text) {
	status.textContent = text;
	progress.hidden = true;
	downloaded.hidden = true;
}

/** @param {Response} response */
function readContent(response) {
	if (!response.body) return response.blob();
	const encoding = response.headers.get('Content-Encoding');
	const total = Number(response.headers.get('Content-Length'));
	const hasTotal = (!encoding || encoding === 'identity') && Number.isSafeInteger(total) && total > 0;
	if (hasTotal) {
		progress.max = total;
		progress.value = 0;
		progress.hidden = false;
	}
	downloaded.textContent = hasTotal ? formatPercent.format(0) : formatBytes.format(0);
	downloaded.hidden = false;
	let bytes = 0;
	const body = response.body.pipeThrough(
		new TransformStream({
			transform(chunk, controller) {
				bytes += chunk.byteLength;
				if (hasTotal) {
					progress.value = bytes;
					const percent = Math.floor((progress.value * 100) / total);
					const label = formatPercent.format(percent / 100);
					if (downloaded.textContent !== label) downloaded.textContent = label;
				} else {
					downloaded.textContent = formatBytes.format(bytes);
				}
				controller.enqueue(chunk);
			}
		})
	);
	return new Response(body, { headers: { 'Content-Type': response.headers.get('Content-Type') ?? '' } }).blob();
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
 * Play B4, Saturn B7: one GET of an optional BIOS (scph5501.bin, saturn_bios.bin), read with a bound of its exact
 * size (cancelled as soon as it's exceeded).
 * @param {string} name the BIOS file name in Paths:Bios
 * @param {number} size the only accepted length
 * @param {string} sizeError the message for a file of any other length
 * @returns {Promise<{ bios: ArrayBuffer | null } | { error: string }>} the firmware, null for HLE (404), or why to stop
 */
async function loadOptionalBios(name, size, sizeError) {
	let response;
	try {
		response = await fetch(`/api/emulator/bios/${name}`, { credentials: 'same-origin' });
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

	const bytes = new Uint8Array(size);
	let length = 0;
	const reader = response.body.getReader();
	try {
		for (;;) {
			const { done, value } = await reader.read();
			if (done) break;
			if (length + value.length > size) {
				await reader.cancel().catch(() => {});
				return { error: sizeError };
			}
			bytes.set(value, length);
			length += value.length;
		}
	} catch {
		return { error: /** @type {string} */ (optionalBiosError(null)) };
	}
	return length === size ? { bios: bytes.buffer } : { error: sizeError };
}

/**
 * B2/B3: one bodyless HEAD of an operator-supplied core's files, before the ROM is fetched or the loader starts.
 * @param {string} core
 * @returns {Promise<string | null>} the message to show, or null when the files are there and match
 */
async function checkOperatorCore(core) {
	try {
		const response = await fetch(operatorCoreCheckUrl(core), { method: 'HEAD', credentials: 'same-origin' });
		return operatorCoreError(core, response.status);
	} catch {
		return operatorCoreError(core, null);
	}
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
		return { data: await readContent(response) };
	} catch {
		return { error: discError(null, null, core) };
	}
}

/**
 * The part of EmulatorJS 4.2.3's game manager the close, export and save-load handlers use.
 * @typedef {{
 *   restart: () => void,
 *   supportsStates: () => boolean,
 *   getState: () => Uint8Array,
 *   loadState: (state: Uint8Array) => void,
 *   getSaveFile: () => Uint8Array | null,
 *   getSaveFilePath: () => string,
 *   loadSaveFiles: () => void,
 *   FS: {
 *     syncfs: (populate: boolean, done: (e?: unknown) => void) => void,
 *     analyzePath: (path: string) => { exists: boolean },
 *     mkdir: (path: string) => void,
 *     unlink: (path: string) => void,
 *     writeFile: (path: string, data: Uint8Array) => void
 *   }
 * }} SaveManager
 */

/** @returns {SaveManager | undefined} */
function gameManager() {
	const emulator = /** @type {{ gameManager?: SaveManager } | undefined} */ (
		/** @type {Record<string, unknown>} */ (/** @type {unknown} */ (window)).EJS_emulator
	);
	return emulator?.gameManager;
}

/** Set by EJS_onGameStart: exports answer `not-started` before it. */
let gameStarted = false;

/** @param {Uint8Array} view @returns {ArrayBuffer} exactly the view's bytes, transferable */
function ownBuffer(view) {
	return /** @type {ArrayBuffer} */ (view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength));
}

/**
 * Saves B18: the running game's state or battery save, or why there is none.
 * @param {'state' | 'battery'} kind
 * @param {boolean} busy the 3DO close sync is running
 * @returns {{ ok: true, bytes: ArrayBuffer, extension: string } | { ok: false, reason: string }}
 */
function exportSave(kind, busy) {
	const manager = gameManager();
	if (!gameStarted || !manager) return { ok: false, reason: 'not-started' };
	if (busy) return { ok: false, reason: 'busy' };
	try {
		if (kind === 'state') {
			if (!manager.supportsStates()) return { ok: false, reason: 'unsupported' };
			const state = manager.getState();
			return state.byteLength === 0
				? { ok: false, reason: 'empty' }
				: { ok: true, bytes: ownBuffer(state), extension: '.state' };
		}
		// getSaveFile() with no argument runs saveSaveFiles() itself and returns null when there's no file.
		const file = manager.getSaveFile();
		if (!file || file.byteLength === 0) return { ok: false, reason: 'empty' };
		const fileName = manager.getSaveFilePath().split('/').pop() ?? '';
		const dot = fileName.lastIndexOf('.');
		return { ok: true, bytes: ownBuffer(file), extension: dot >= 0 ? fileName.slice(dot).toLowerCase() : '.srm' };
	} catch {
		return { ok: false, reason: 'error' };
	}
}

/**
 * Saves B17: puts a library save into the started game, as EmulatorJS 4.2.3's own import does for a battery save,
 * then restarts and syncs so the in-browser save for this release is replaced at once.
 * @param {'state' | 'battery'} kind
 * @param {ArrayBuffer} bytes
 */
function applySave(kind, bytes) {
	const manager = gameManager();
	try {
		if (!manager) throw new Error('no game manager');
		if (kind === 'state') {
			if (!manager.supportsStates()) return show("This emulator can't load save states.");
			manager.loadState(new Uint8Array(bytes));
			return;
		}
		const path = manager.getSaveFilePath();
		let folder = '';
		for (const part of path.split('/').slice(0, -1)) {
			if (part === '') continue;
			folder += `/${part}`;
			if (!manager.FS.analyzePath(folder).exists) manager.FS.mkdir(folder);
		}
		if (manager.FS.analyzePath(path).exists) manager.FS.unlink(path);
		manager.FS.writeFile(path, new Uint8Array(bytes));
		manager.loadSaveFiles();
		manager.restart();
		manager.FS.syncfs(false, () => {});
	} catch {
		show("Couldn't load the save.");
	}
}

/**
 * Saves B17: the library save, fetched before the loader is added.
 * @param {string} game
 * @param {string} id
 * @returns {Promise<{ bytes: ArrayBuffer } | { error: string }>}
 */
async function loadLibrarySave(game, id) {
	try {
		const response = await fetch(`/api/games/${game}/saves/${id}/download`, { credentials: 'same-origin' });
		if (response.status === 401) return { error: sessionExpired };
		if (!response.ok) return { error: "Couldn't load the save." };
		return { bytes: await response.arrayBuffer() };
	} catch {
		return { error: "Couldn't load the save." };
	}
}

/** 3DO B11: how long Opera's reset gets to write its NVRAM file before /data/saves is synced. */
const saveSyncDelayMs = 250;

/**
 * The page's one message listener, from the modal (window.parent, same origin) only. 3DO B11: answers the close
 * request on Opera; its reset writes the NVRAM file, and the sync then persists /data/saves (EmulatorJS's IDBFS mount)
 * before the reply lets the modal remove the iframe. Saves B18: answers an export request on every core; an export is
 * synchronous, so a close request never interleaves with one.
 * @param {boolean} isThreeDo
 */
function listenForMessages(isThreeDo) {
	/** @type {Promise<void> | null} */
	let saving = null;
	const reply = () => window.parent.postMessage({ type: 'rnrm-emulator-closed' }, location.origin);
	window.addEventListener('message', (event) => {
		if (event.source !== window.parent || event.origin !== location.origin) return;
		const data = event.data;
		if (data === null || typeof data !== 'object') return;
		const keys = Object.keys(data);
		const isExport =
			keys.length === 2 &&
			data.type === 'rnrm-emulator-export' &&
			(data.kind === 'state' || data.kind === 'battery');
		if (isExport) {
			const result = exportSave(data.kind, saving !== null);
			const message = { type: 'rnrm-emulator-export-result', kind: data.kind, ...result };
			window.parent.postMessage(message, location.origin, result.ok ? [result.bytes] : []);
			return;
		}
		const isClose = keys.length === 1 && data.type === 'rnrm-emulator-close';
		if (!isClose || !isThreeDo) return;
		if (saving) return;
		const manager = gameManager();
		if (!manager) return reply();
		saving = new Promise((resolve) => {
			try {
				manager.restart();
			} catch {
				// Saved or not, the modal may close.
			}
			setTimeout(() => {
				try {
					manager.FS.syncfs(false, () => resolve(undefined));
				} catch {
					resolve(undefined);
				}
			}, saveSyncDelayMs);
		}).then(() => {
			saving = null;
			reply();
		});
	});
}

async function start() {
	const pspPage = document.body.dataset.player === 'psp';
	const params = parsePlayParams(location.search, pspPage);
	if (!params) return show('Invalid emulator link.');

	const isPsx = params.core === 'pcsx_rearmed';
	const isSaturn = params.core === 'yabause';
	const isPsp = params.core === pspCore;
	const isDs = params.core === 'desmume';
	const isSegaCd = params.core === 'genesis_plus_gx' && params.disc;
	const isThreeDo = params.core === 'opera';
	// PSP B3: play-psp.html is its own tab, with no modal to talk to.
	if (!pspPage) listenForMessages(isThreeDo);
	if (isPsp) {
		const problem = pspBrowserError(pspBrowser());
		if (problem) return show(problem);
	}
	show('Preparing emulator…');
	const isOperator = isOperatorCore(params.core);
	if (isOperator) {
		const problem = await checkOperatorCore(params.core);
		if (problem) return show(problem);
	}
	const bios = biosFor(params.core);
	if (bios) {
		const problem = await checkBios(bios);
		if (problem) return show(problem);
	}
	// Play B4, Saturn B7: the optional firmware, handed over in memory; a 404 means the core's HLE BIOS.
	const optional = isPsx
		? { name: psxBios, bytes: psxBiosBytes, sizeError: psxBiosSizeError }
		: isSaturn
			? { name: saturnBios, bytes: saturnBiosBytes, sizeError: saturnBiosSizeError }
			: null;
	/** @type {ArrayBuffer | null} */
	let firmware = null;
	if (optional) {
		const result = await loadOptionalBios(optional.name, optional.bytes, optional.sizeError);
		if ('error' in result) return show(result.error);
		firmware = result.bios;
	}
	// Sega CD B7: U, E, then J, one at a time; a 404 skips a file, any other problem stops the launch there.
	/** @type {Record<string, ArrayBuffer>} */
	const segaCdFirmware = {};
	if (isSegaCd) {
		for (const name of segaCdBios) {
			const result = await loadOptionalBios(name, segaCdBiosBytes, segaCdBiosSizeError(name));
			if ('error' in result) return show(result.error);
			if (result.bios) segaCdFirmware[`/${name}`] = result.bios;
		}
		if (Object.keys(segaCdFirmware).length === 0) return show(segaCdBiosMissing);
	}
	// 3DO B8: the five names in order, one at a time; a 404 skips a file, any other problem stops the launch there.
	/** @type {Record<string, ArrayBuffer>} */
	const threeDoFirmware = {};
	if (isThreeDo) {
		for (const name of threeDoBios) {
			const result = await loadOptionalBios(name, threeDoBiosBytes, threeDoBiosSizeError(name));
			if ('error' in result) return show(result.error);
			if (result.bios) threeDoFirmware[`/${name}`] = result.bios;
		}
		if (Object.keys(threeDoFirmware).length === 0) return show(threeDoBiosMissing);
	}

	/** @type {Blob} */
	let content;
	// Sega CD B6: `disc=1` also loads from play-disc, for a core (Genesis Plus GX) that plays cartridges too.
	// 3DO B7: Opera plays only discs, so it loads from play-disc with or without `disc=1`, like Yabause.
	if (isPsx || isPsp || isSaturn || isThreeDo || params.disc) {
		show('Downloading disc…');
		const disc = await loadDisc(`/api/games/${params.game}/roms/${params.rom}/play-disc`, params.core);
		if ('error' in disc) return show(disc.error);
		content = disc.data;
	} else {
		show('Downloading ROM…');
		const response = await fetch(`/api/games/${params.game}/roms/${params.rom}/download`, {
			credentials: 'same-origin'
		});
		if (!response.ok)
			return show(
				response.status === 401 ? sessionExpired : `Couldn't load the ROM (HTTP ${response.status}).`
			);
		content = await readContent(response);
	}
	// Saves B17: the library save comes before the loader; a failure stops the launch here.
	/** @type {ArrayBuffer | null} */
	let saveBytes = null;
	if (params.save) {
		show('Downloading save…');
		const save = await loadLibrarySave(params.game, params.save.id);
		if ('error' in save) return show(save.error);
		saveBytes = save.bytes;
	}
	show('Starting emulator…');
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
	// PCSX ReARMed and Yabause find their firmware in the content root (EmulatorJS sets no system directory);
	// GameManager writes this key before loading the content.
	if (optional && firmware) ejs.EJS_externalFiles = { [`/${optional.name}`]: firmware };
	// Sega CD B7/B9: Genesis Plus GX also looks in the content root, for bios_CD_U/E/J.bin. EmulatorJS 4.2.3 disables
	// cue generation for this core, so a lone .bin would load as a cartridge: keep it on for a disc.
	if (isSegaCd) {
		ejs.EJS_externalFiles = segaCdFirmware;
		ejs.EJS_disableCue = false;
	}
	// 3DO B8: Opera reads its BIOS from the content root too, and keeps cue handling on by itself; it boots the first
	// BIOS present in its own order, so no core option is set.
	if (isThreeDo) ejs.EJS_externalFiles = threeDoFirmware;
	// B3/B8: an operator-supplied core loads its payloads and report by name from the API; the vendored loader keeps
	// them non-threaded.
	if (isOperator) ejs.EJS_paths = operatorCorePaths(params.core);
	// Pinned cores (SMS Plus GX, FCEUmm, PCSX ReARMed, Genesis Plus GX, PicoDrive, Yabause, Snes9x, DeSmuME): hide the
	// core selector. DeSmuME gets no EJS_biosUrl or external files: it uses its built-in BIOS and firmware.
	// Nintendo DS B4: DeSmuME also hides the threads setting; the vendored loader never picks a threaded payload.
	if (hidesCoreChooser(params.core))
		ejs.EJS_hideSettings = isDs ? ['retroarch_core', 'ejs_threads'] : ['retroarch_core'];
	const scheme = controlSchemeFor(params.core, params.name);
	if (scheme) ejs.EJS_controlScheme = scheme;
	ejs.EJS_Buttons = {
		saveState: false,
		loadState: false,
		quickSave: false,
		quickLoad: false,
		netplay: false,
		cheat: false,
		// 3DO B11: EmulatorJS's Exit resets Opera, which writes its NVRAM file.
		exitEmulation: isThreeDo,
		// Nintendo DS B4: the save-file export and import use RetroArch's SRAM path, which DeSmuME never writes.
		...(isDs ? { saveSavFiles: false, loadSavFiles: false } : {})
	};
	ejs.EJS_defaultOptions = {
		'save-save-interval': '30',
		// Nintendo DS B4: both screens, lower below; a pointer press is a stylus press; no pointer lock on click.
		// Saved per-game settings override these, as for every core.
		...(isDs
			? {
					desmume_screens_layout: 'top/bottom',
					desmume_pointer_mouse: 'enabled',
					desmume_pointer_type: 'touch',
					lockMouse: 'disabled'
				}
			: {})
	};
	// PSP B3: PPSSPP has only a threaded build; the vendored loader also ignores a saved "threads disabled".
	if (isPsp) ejs.EJS_threads = true;
	// Saves B17/B18: loader.js runs this on `start`, once at the end of startGame() (restart() doesn't fire it); the
	// save goes in 10 ms later, as EmulatorJS's own start state does, and only once.
	let saveApplied = false;
	ejs.EJS_onGameStart = () => {
		gameStarted = true;
		if (!params.save || !saveBytes || saveApplied) return;
		saveApplied = true;
		const { kind } = params.save;
		const bytes = saveBytes;
		setTimeout(() => applySave(kind, bytes), 10);
	};

	const loader = document.createElement('script');
	loader.src = '/emulator/vendor/data/loader.js';
	loader.addEventListener('error', () => show("Couldn't load the emulator."));
	loader.addEventListener('load', () => show(''));
	document.body.append(loader);
}

/** Settles once the page has started the loader or shown why it couldn't (tests await it). */
export const started = start().catch(() => show("Couldn't load the ROM."));
