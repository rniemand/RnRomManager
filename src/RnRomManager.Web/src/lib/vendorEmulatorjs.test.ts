import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
	existsSync,
	mkdirSync,
	mkdtempSync,
	readFileSync,
	readdirSync,
	rmSync,
	writeFileSync
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, sep } from 'node:path';
import { runInNewContext } from 'node:vm';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

// Browser emulators B9: the vendored file list and the concatenated emulator.min.js.
const pkg = join('node_modules', '@emulatorjs', 'emulatorjs', 'data');
const loaderOrder = [
	'emulator.js',
	'nipplejs.js',
	'shaders.js',
	'storage.js',
	'gamepad.js',
	'GameManager.js',
	'socket.io.min.js',
	'compression.js'
];

type EjsPrototype = {
	getCores: () => Record<string, string[]>;
	getCore: (this: object, generic?: boolean) => string;
};

/** EmulatorJS's getCore() from emulator.js source (or the bundle, which starts with it), run outside a browser. */
function coreResolver(
	source: string
): (system: string, savedCore: string | null, generic?: boolean) => string {
	const end = source.indexOf('window.EmulatorJS = EmulatorJS;');
	expect(end).toBeGreaterThan(0);
	const proto = runInNewContext(`${source.slice(0, end)}EmulatorJS.prototype`, {}) as EjsPrototype;
	return (system, savedCore, generic) =>
		proto.getCore.call(
			{
				config: { system },
				getCores: proto.getCores,
				getCore: proto.getCore,
				preGetSetting: (name: string) => (name === 'retroarch_core' ? savedCore : null)
			},
			generic
		);
}

// The reviewed notices and licence texts in emulator-licenses/ (2600/Coleco/GG spec step 6, NES/PSX spec step 1).
const coreLicenses = [
	'COMMON-BUILD.txt',
	'azahar-LICENSE.txt',
	'azahar-NOTICE.txt',
	'azahar-boost-LICENSE.txt',
	'azahar-cryptopp-LICENSE.txt',
	'azahar-dds-ktx-LICENSE.txt',
	'azahar-faad2-LICENSE.txt',
	'azahar-fmt-LICENSE.txt',
	'azahar-httplib-LICENSE.txt',
	'azahar-json-LICENSE.txt',
	'azahar-libretro-common-LICENSE.txt',
	'azahar-lodepng-LICENSE.txt',
	'azahar-nihstro-LICENSE.txt',
	'azahar-soundtouch-LICENSE.txt',
	'azahar-teakra-LICENSE.txt',
	'azahar-xxhash-LICENSE.txt',
	'azahar-zstd-LICENSE.txt',
	'desmume-LICENSE.txt',
	'desmume-NOTICE.txt',
	'desmume-emufile-LICENSE.txt',
	'desmume-libfat-LICENSE.txt',
	'desmume-libretro-common-LICENSE.txt',
	'desmume-mono-LICENSE.txt',
	'desmume-tinyxml-LICENSE.txt',
	'desmume-xbrz-LICENSE.txt',
	'dosbox_pure-LICENSE.txt',
	'dosbox_pure-NOTICE.txt',
	'dosbox_pure-libretro-common-LICENSE.txt',
	'dosbox_pure-mame-LICENSE.txt',
	'dosbox_pure-mt32emu-LICENSE.txt',
	'dosbox_pure-nukedopl3-LICENSE.txt',
	'dosbox_pure-stb_vorbis-LICENSE.txt',
	'dosbox_pure-tsf-LICENSE.txt',
	'fceumm-LICENSE.txt',
	'fceumm-NOTICE.txt',
	'gearcoleco-LICENSE.txt',
	'gearcoleco-NOTICE.txt',
	'mednafen_ngp-LICENSE.txt',
	'mednafen_ngp-NOTICE.txt',
	'mednafen_ngp-libretro-common-LICENSE.txt',
	'mednafen_pce-LICENSE.txt',
	'mednafen_pce-NOTICE.txt',
	'mednafen_pce-libchdr-LICENSE.txt',
	'mednafen_pce-lzma-LICENSE.txt',
	'mednafen_pce-tremor-LICENSE.txt',
	'pcsx_rearmed-LICENSE.txt',
	'pcsx_rearmed-NOTICE.txt',
	'pcsx_rearmed-libchdr-LICENSE.txt',
	'pcsx_rearmed-zstd-LICENSE.txt',
	'ppsspp-LICENSE.txt',
	'ppsspp-NOTICE.txt',
	'puae-LICENSE.txt',
	'puae-NOTICE.txt',
	'puae-libchdr-LICENSE.txt',
	'retroarch-LICENSE.txt',
	'smsplus-LICENSE.txt',
	'smsplus-NOTICE.txt',
	'smsplus-sms_ntsc-LICENSE.txt',
	'stella2014-LICENSE.txt',
	'stella2014-NOTICE.txt',
	'yabause-LICENSE.txt',
	'yabause-NOTICE.txt',
	'yabause-flac-LICENSE.txt',
	'yabause-libchdr-LICENSE.txt'
];

// The guarded vendoring edits (anchor → its replacement), applied in order.
const getCoreAnchor = '        const gen = this.getCore(true);\n';
const pinGuard =
	'        if (["smsplus", "fceumm", "pcsx_rearmed", "genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(core)) {\n' +
	'            return core;\n' +
	'        }\n';
const legacyAnchor =
	'            let legacy = (this.supportsWebgl2 && this.webgl2Enabled ? "" : "-legacy");\n';
const threadOverride =
	'            if (["ppsspp", "dosbox_pure"].includes(this.getCore()) && typeof window.SharedArrayBuffer === "function") {\n' +
	'                threads = true;\n' +
	'            }\n' +
	'            if (["genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())) {\n' +
	'                threads = false;\n' +
	'            }\n';
const cdnAnchor =
	'            if (res === -1) {\n' +
	'                console.log("File not found, attemping to fetch from emulatorjs cdn.");\n';
const cdnSkip =
	'                if (["genesis_plus_gx", "picodrive", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())) {\n' +
	'                    this.startGameError(this.localization("Error downloading core") + " (" + filename + ")");\n' +
	'                    return;\n' +
	'                }\n';
const assetsRequest =
	'            this.EJS.downloadFile("cores/ppsspp-assets.zip", null, false, { responseType: "arraybuffer", method: "GET" }).then((res) => {\n';
const assetsError =
	'this.EJS.startGameError(this.EJS.localization("Error downloading core") + " (ppsspp-assets.zip)");\n';
// Nintendo DS B4: saveSaveFiles() syncs /data/saves for DeSmuME only (upstream comments the call out).
const syncfsAnchor = '        //this.FS.syncfs(false, () => {});\n';
const syncfsEdit =
	'        if (this.EJS.getCore() === "desmume") {\n' +
	'            this.FS.syncfs(false, (err) => { if (err) console.warn(err); });\n' +
	'        }\n';
// Saturn and Sega CD multi-disc S4: Yabause and Genesis Plus GX boot an unpacked .m3u before the image/cue choice.
const isoAnchor =
	'                    if (isoFile !== null && (supportsExt("iso") || supportsExt("cso") || supportsExt("chd") || supportsExt("elf"))) {\n';
const m3uFirst =
	'                    const m3uFile = fileNames.find((name) => name.split(".").pop().toLowerCase() === "m3u");\n' +
	'                    if (m3uFile !== undefined && ["yabause", "genesis_plus_gx"].includes(this.getCore())) {\n' +
	'                        this.fileName = m3uFile;\n' +
	'                    } else if (isoFile !== null && (supportsExt("iso") || supportsExt("cso") || supportsExt("chd") || supportsExt("elf"))) {\n';
// MS-DOS P2: DOSBox Pure mounts the downloaded ZIP whole, so it is written to the content name unpacked.
const altNameAnchor = '                const altName = this.getBaseFileName(true);\n';
const dosboxWhole =
	'                if (this.getCore() === "dosbox_pure") {\n' +
	'                    this.gameManager.FS.writeFile(altName, new Uint8Array(data));\n' +
	'                    this.fileName = altName;\n' +
	'                    resolve();\n' +
	'                    return;\n' +
	'                }\n';
const guardedEdits: Record<string, [string, string][]> = {
	'emulator.js': [
		[altNameAnchor, altNameAnchor + dosboxWhole],
		[getCoreAnchor, pinGuard + getCoreAnchor],
		[legacyAnchor, threadOverride + legacyAnchor],
		[cdnAnchor, cdnAnchor + cdnSkip],
		[isoAnchor, m3uFirst],
		[
			'                resolve(new Uint8Array(await data.arrayBuffer()));\n',
			'                let bytes;\n' +
				'                try {\n' +
				'                    bytes = await data.arrayBuffer();\n' +
				'                } catch (e) {\n' +
				'                    if (!["ppsspp", "desmume", "dosbox_pure"].includes(this.getCore())) throw e;\n' +
				'                    this.startGameError(this.localization("Error reading the game file"));\n' +
				'                    return;\n' +
				'                }\n' +
				'                resolve(new Uint8Array(bytes));\n'
		]
	],
	'GameManager.js': [
		[
			assetsRequest,
			assetsRequest +
				'                if (res === -1) {\n' +
				'                    ' +
				assetsError +
				'                    return;\n' +
				'                }\n'
		],
		[
			'                        this.EJS.textElem.innerText = this.localization("Network Error");\n' +
				'                        this.EJS.textElem.style.color = "red";\n',
			'                        ' + assetsError
		],
		[syncfsAnchor, syncfsEdit]
	]
};

// 3DS V2–V3: the EmulatorJS 4.3.0-pre files vendored under ejs-4.3/data/, and its one guarded edit.
const ejs43Files = [
	'loader.js',
	'emulator.css',
	'version.json',
	'src/GameManager.js',
	'src/cache.js',
	'src/compression.js',
	'src/consts.js',
	'src/emulator.js',
	'src/gamepad.js',
	'src/license.js',
	'src/netplay.js',
	'src/setup.js',
	'src/shaders.js',
	'src/storage.js',
	'src/utils.js',
	'src/vendor/nipplejs.js',
	'src/vendor/socket.io.min.js',
	'compression/extract7z.js',
	'compression/extractzip.js',
	'compression/libunrar.js',
	'compression/libunrar.wasm'
];
const fileReadGuard =
	'                let arrayBuffer;\n' +
	'                try {\n' +
	'                    arrayBuffer = await url.arrayBuffer();\n' +
	'                } catch (e) {\n' +
	'                    this.startGameError(this.localization("Error reading the game file"));\n' +
	'                    return;\n' +
	'                }\n';

const tick = () => new Promise((r) => setTimeout(r, 0));

/** One EmulatorJS method from emulator.js source (or the bundle, which starts with it), run outside a browser. */
function emulatorMethod(
	source: string,
	name: string
): (this: object, ...args: unknown[]) => unknown {
	const end = source.indexOf('window.EmulatorJS = EmulatorJS;');
	expect(end).toBeGreaterThan(0);
	const proto = runInNewContext(`${source.slice(0, end)}EmulatorJS.prototype`, {
		Blob,
		ArrayBuffer,
		Uint8Array
	}) as Record<string, (this: object, ...args: unknown[]) => unknown>;
	return proto[name];
}

type CoreDownload = { requests: string[]; errors: string[] };

/**
 * SNES B3: runs EmulatorJS's downloadGameCore() from emulator.js source (or the bundle) for an operator core outside a
 * browser, with the report and payload requests stubbed, and records the requested paths and startup errors.
 * Decompression never settles, so the run stops once the payload is handed over.
 */
async function downloadCore(
	source: string,
	core: string,
	{
		supportsWebgl2 = true,
		webgl2Enabled = null as boolean | null,
		savedThreads = null as string | null,
		savedCore = null as string | null,
		threads = false,
		report = async (): Promise<unknown> => ({ data: { buildStart: 1, options: {} } }),
		payload = async (): Promise<unknown> => ({ data: new ArrayBuffer(4) })
	} = {}
): Promise<CoreDownload> {
	const end = source.indexOf('window.EmulatorJS = EmulatorJS;');
	expect(end).toBeGreaterThan(0);
	const proto = runInNewContext(`${source.slice(0, end)}EmulatorJS.prototype`, {
		window: { SharedArrayBuffer },
		console: { log: () => {}, warn: () => {}, error: () => {} },
		Uint8Array,
		TextDecoder
	}) as Record<string, (this: object, ...args: unknown[]) => unknown>;
	const run: CoreDownload = { requests: [], errors: [] };
	const ejs = {
		config: { system: core, threads },
		debug: true,
		ejs_version: '4.2.3',
		supportsWebgl2,
		webgl2Enabled,
		textElem: {},
		storage: { core: { get: async () => null, put: () => {} } },
		getCores: proto.getCores,
		getCore: proto.getCore,
		requiresThreads: proto.requiresThreads,
		requiresWebGL2: proto.requiresWebGL2,
		localization: (t: string) => t,
		preGetSetting: (name: string) =>
			name === 'ejs_threads' ? savedThreads : name === 'retroarch_core' ? savedCore : null,
		startGameError: (m: string) => run.errors.push(m),
		checkCompression: () => new Promise(() => {}),
		downloadFile: (path: string) => {
			run.requests.push(path);
			return path.startsWith('cores/reports/') ? report() : payload();
		}
	};
	proto.downloadGameCore.call(ejs);
	for (let i = 0; i < 5; i++) await tick();
	return run;
}

/** EJS_GameManager's prototype from GameManager.js, run outside a browser. */
type GameManagerPrototype = {
	loadPpssppAssets: (this: object) => Promise<void>;
	saveSaveFiles: (this: object) => void;
};

function gameManagerPrototype(source: string): GameManagerPrototype {
	const window: Record<string, { prototype: GameManagerPrototype }> = {};
	runInNewContext(source, { window });
	return window.EJS_GameManager.prototype;
}

function files(dir: string): string[] {
	return readdirSync(dir, { recursive: true, withFileTypes: true })
		.filter((e) => e.isFile())
		.map((e) => relative(dir, join(e.parentPath, e.name)).replaceAll(sep, '/'))
		.sort();
}

describe('vendor-emulatorjs', () => {
	let out: string;

	beforeAll(() => {
		out = mkdtempSync(join(tmpdir(), 'ejs-'));
		execFileSync(process.execPath, ['scripts/vendor-emulatorjs.mjs', out]);
	});

	afterAll(() => rmSync(out, { recursive: true, force: true }));

	it('writes exactly the B9 files', () => {
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
			'yabause',
			'desmume',
			'puae',
			'mednafen_pce',
			'mednafen_ngp'
		].flatMap((c) => [
			`data/cores/${c}-legacy-wasm.data`,
			`data/cores/${c}-wasm.data`,
			`data/cores/reports/${c}.json`
		]);
		const compression = readdirSync(join(pkg, 'compression'))
			.filter((f) => f !== 'README.md')
			.map((f) => `data/compression/${f}`);
		expect(files(out)).toEqual(
			[
				'data/loader.js',
				'data/emulator.css',
				'data/emulator.min.css',
				'data/emulator.min.js',
				'data/version.json',
				...loaderOrder.map((f) => `data/src/${f}`),
				...compression,
				...cores,
				'data/cores/ppsspp-assets.zip',
				'data/cores/ppsspp-thread-wasm.data',
				'data/cores/reports/ppsspp.json',
				'data/cores/dosbox_pure-thread-wasm.data',
				'data/cores/dosbox_pure-thread-legacy-wasm.data',
				'data/cores/reports/dosbox_pure.json',
				...ejs43Files.map((f) => `ejs-4.3/data/${f}`),
				'ejs-4.3/data/cores/azahar-thread-wasm.data',
				'ejs-4.3/data/cores/reports/azahar.json',
				'licenses/emulatorjs.txt',
				'licenses/emulatorjs-4.3.txt',
				...coreLicenses.map((f) => `licenses/${f}`)
			].sort()
		);
		// SNES B3: the operator supplies Snes9x, so none of its files is vendored.
		expect(files(out).filter((f) => f.includes('snes9x'))).toEqual([]);
		// 3DO B6: nor Opera.
		expect(files(out).filter((f) => f.includes('opera'))).toEqual([]);
		// C64 B7: nor VICE.
		expect(files(out).filter((f) => f.includes('vice'))).toEqual([]);
		// PSP B3, MS-DOS P1, 3DS V2: PPSSPP's, DOSBox Pure's and azahar's threaded payloads are the only ones; every
		// other core is single-threaded.
		expect(files(out).filter((f) => f.includes('-thread'))).toEqual([
			'data/cores/dosbox_pure-thread-legacy-wasm.data',
			'data/cores/dosbox_pure-thread-wasm.data',
			'data/cores/ppsspp-thread-wasm.data',
			'ejs-4.3/data/cores/azahar-thread-wasm.data'
		]);
		for (const f of [
			'dosbox_pure-thread-wasm.data',
			'dosbox_pure-thread-legacy-wasm.data',
			'reports/dosbox_pure.json'
		])
			expect(
				readFileSync(join(out, 'data', 'cores', f)).equals(
					readFileSync(join('node_modules', '@emulatorjs', 'core-dosbox_pure', f))
				),
				f
			).toBe(true);
		for (const f of ['ppsspp-thread-wasm.data', 'ppsspp-assets.zip', 'reports/ppsspp.json'])
			expect(
				readFileSync(join(out, 'data', 'cores', f)).equals(
					readFileSync(join('node_modules', '@emulatorjs', 'core-ppsspp', f))
				),
				f
			).toBe(true);
	});

	it('copies the src scripts unchanged except the guarded edits in emulator.js and GameManager.js', () => {
		for (const f of loaderOrder.filter((f) => !(f in guardedEdits)))
			expect(readFileSync(join(out, 'data', 'src', f), 'utf8')).toBe(
				readFileSync(join(pkg, 'src', f), 'utf8')
			);
		for (const [f, edits] of Object.entries(guardedEdits)) {
			let expected = readFileSync(join(pkg, 'src', f), 'utf8');
			for (const [anchor, replacement] of edits) {
				expect(expected.split(anchor), anchor).toHaveLength(2);
				expected = expected.replace(anchor, () => replacement);
			}
			expect(readFileSync(join(out, 'data', 'src', f), 'utf8'), f).toBe(expected);
		}
	});

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s forces the threaded payload for PPSSPP and DOSBox Pure only, despite a saved ejs_threads setting',
		(file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const select = source.indexOf(legacyAnchor);
			const override = source.lastIndexOf(threadOverride, select);
			// After the saved-setting branch, right before the payload name is built.
			expect(override).toBeGreaterThan(
				source.lastIndexOf('const opt = this.preGetSetting("ejs_threads");', select)
			);
			expect(override + threadOverride.length).toBe(select);
		}
	);

	/** Saturn and Sega CD S4: the content file downloadRom() picks from unpacked `names` for `core`. */
	async function pickedContent(
		file: string,
		core: string,
		names: string[],
		extensions: string[]
	): Promise<string> {
		const source = readFileSync(join(out, 'data', file), 'utf8');
		const end = source.indexOf('window.EmulatorJS = EmulatorJS;');
		expect(end).toBeGreaterThan(0);
		const proto = runInNewContext(`${source.slice(0, end)}EmulatorJS.prototype`, {
			File,
			Blob,
			Uint8Array,
			ArrayBuffer,
			console: { log: () => {}, warn: () => {}, error: () => {} }
		}) as Record<string, (this: object, ...args: unknown[]) => unknown>;
		// The prototype's other methods (toData) stay; the stubs below replace what needs a browser.
		const ejs = Object.assign(Object.create(proto) as Record<string, unknown>, {
			debug: true,
			config: { gameUrl: 'game.zip' },
			extensions,
			textElem: { innerText: '' },
			fileName: '',
			getCore: (generic?: boolean) => (generic ? 'generic' : core),
			getBaseFileName: () => 'game',
			localization: (t: string) => t,
			saveInBrowserSupported: () => false,
			downloadFile: async () => ({ data: new ArrayBuffer(4), headers: {} }),
			checkCompression: async (
				_: unknown,
				__: string,
				add: (name: string, data: Uint8Array) => void
			) => {
				for (const name of names) add(name, new Uint8Array(1));
			},
			gameManager: {
				FS: { writeFile: () => {}, analyzePath: () => ({ exists: true }), mkdir: () => {} },
				createCueFile: () => 'generated.cue'
			}
		});
		await proto.downloadRom.call(ejs);
		return ejs.fileName as string;
	}

	const setNames = [
		'Game (USA).m3u',
		'Game (USA) (Disc 1).cue',
		'disc1-track-01.bin',
		'Game (USA) (Disc 2).chd'
	];

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s boots the .m3u of a ZIP for Yabause and Genesis Plus GX only (Saturn and Sega CD S4)',
		async (file) => {
			const yabause = ['cue', 'iso', 'ccd', 'mds', 'chd', 'zip', 'm3u'];
			const genesis = ['m3u', 'md', 'bin', 'cue', 'iso', 'chd'];
			expect(await pickedContent(file, 'yabause', setNames, yabause)).toBe('Game (USA).m3u');
			expect(await pickedContent(file, 'genesis_plus_gx', setNames, genesis)).toBe(
				'Game (USA).m3u'
			);
			// Upstream's choice: an image the core lists beats the cue, for another core or without an .m3u.
			expect(await pickedContent(file, 'opera', setNames, ['iso', 'chd', 'cue', 'bin'])).toBe(
				'Game (USA) (Disc 2).chd'
			);
			expect(await pickedContent(file, 'yabause', setNames.slice(1), yabause)).toBe(
				'Game (USA) (Disc 2).chd'
			);
			expect(
				await pickedContent(file, 'genesis_plus_gx', ['Game (USA).cue', 'track-01.bin'], genesis)
			).toBe('Game (USA).cue');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s hands DOSBox Pure the whole ZIP under the content name, unpacked for other cores (MS-DOS P2)',
		async (file) => {
			const names = ['INSTALL.BAT', 'GAME.EXE'];
			expect(await pickedContent(file, 'dosbox_pure', names, ['zip', 'dosz', 'exe', 'bat'])).toBe(
				'game'
			);
			expect(await pickedContent(file, 'pcsx_rearmed', ['Game.exe'], ['exe'])).toBe('Game.exe');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s picks a threaded DOSBox Pure payload despite a saved disabled threads setting (MS-DOS P2)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const run = await downloadCore(source, 'dosbox_pure', {
				threads: true,
				savedThreads: 'disabled'
			});
			expect(run.errors).toEqual([]);
			expect(run.requests[1]).toMatch(/^cores\/dosbox_pure-thread-(legacy-)?wasm\.data$/);
			// Without EJS_threads upstream refuses a core that requires threads (P7 sets it).
			expect((await downloadCore(source, 'dosbox_pure')).errors).toEqual([
				'Error for site owner\nCheck console'
			]);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s shows a startup error when PPSSPP reads a File that rejects, and rethrows for other cores',
		async (file) => {
			const toData = emulatorMethod(readFileSync(join(out, 'data', file), 'utf8'), 'toData');
			const failing = new Blob(['iso']);
			failing.arrayBuffer = () => Promise.reject(new Error('read failed'));
			const errors: string[] = [];
			const ejs = (core: string) => ({
				getCore: () => core,
				localization: (t: string) => t,
				startGameError: (m: string) => errors.push(m)
			});

			const pending = toData.call(ejs('ppsspp'), failing) as Promise<unknown>;
			const settled = await Promise.race([
				pending.then(() => 'settled'),
				tick().then(() => 'pending')
			]);
			expect(settled).toBe('pending');
			expect(errors).toEqual(['Error reading the game file']);
			expect(await toData.call(ejs('ppsspp'), new Blob([new Uint8Array([1, 2])]))).toEqual(
				new Uint8Array([1, 2])
			);

			const rejections: unknown[] = [];
			const onRejection = (e: unknown) => rejections.push(e);
			process.on('unhandledRejection', onRejection);
			try {
				void toData.call(ejs('pcsx_rearmed'), failing);
				await tick();
				await tick();
			} finally {
				process.off('unhandledRejection', onRejection);
			}
			expect(rejections).toHaveLength(1);
			expect(errors).toHaveLength(1);
		}
	);

	it.each([
		['a failed request', () => Promise.resolve(-1), () => Promise.resolve({})],
		[
			'an unreadable archive',
			() => Promise.resolve({ data: new ArrayBuffer(1) }),
			() => Promise.resolve(-1)
		]
	])(
		'GameManager.js shows a startup error for %s of ppsspp-assets.zip',
		async (_, download, decompress) => {
			const errors: string[] = [];
			const manager = gameManagerPrototype(
				readFileSync(join(out, 'data', 'src', 'GameManager.js'), 'utf8')
			);
			void manager.loadPpssppAssets.call({
				EJS: {
					downloadFile: download,
					checkCompression: decompress,
					localization: (t: string) => t,
					startGameError: (m: string) => errors.push(m),
					textElem: { style: {} }
				}
			});
			await tick();
			await tick();
			expect(errors).toEqual(['Error downloading core (ppsspp-assets.zip)']);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s keeps an explicit smsplus despite a saved alternate core',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			expect(resolve('smsplus', 'genesis_plus_gx')).toBe('smsplus');
			expect(resolve('smsplus', 'picodrive')).toBe('smsplus');
			expect(resolve('smsplus', null)).toBe('smsplus');
			expect(resolve('smsplus', null, true)).toBe('segaMS');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s keeps an explicit fceumm or pcsx_rearmed despite a saved alternate core',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			// Upstream switches to the saved alternative, which isn't vendored.
			expect(upstream('fceumm', 'nestopia')).toBe('nestopia');
			expect(upstream('pcsx_rearmed', 'mednafen_psx_hw')).toBe('mednafen_psx_hw');
			expect(resolve('fceumm', 'nestopia')).toBe('fceumm');
			expect(resolve('fceumm', null)).toBe('fceumm');
			expect(resolve('pcsx_rearmed', 'mednafen_psx_hw')).toBe('pcsx_rearmed');
			expect(resolve('pcsx_rearmed', null)).toBe('pcsx_rearmed');
			// Control and settings groups still come from the generic lookup.
			expect(resolve('fceumm', 'nestopia', true)).toBe('nes');
			expect(resolve('pcsx_rearmed', null, true)).toBe('psx');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s keeps an explicit genesis_plus_gx despite a saved alternate core (Genesis B5)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			// Upstream resolves genesis_plus_gx's first group, segaMS, so a saved SMS Plus GX or PicoDrive wins.
			expect(upstream('genesis_plus_gx', 'smsplus')).toBe('smsplus');
			expect(upstream('genesis_plus_gx', 'picodrive')).toBe('picodrive');
			for (const saved of ['smsplus', 'picodrive', null])
				expect(resolve('genesis_plus_gx', saved), `${saved}`).toBe('genesis_plus_gx');
			expect(resolve('genesis_plus_gx', null, true)).toBe('segaMS');
			// The other pinned cores keep theirs, whatever was saved while playing Genesis.
			expect(resolve('smsplus', 'genesis_plus_gx')).toBe('smsplus');
			expect(resolve('pcsx_rearmed', 'genesis_plus_gx')).toBe('pcsx_rearmed');
			expect(resolve('fceumm', 'genesis_plus_gx')).toBe('fceumm');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s keeps an explicit picodrive despite a saved alternate core (32X B3)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			// Upstream resolves picodrive's first group, segaMS, so a saved SMS Plus GX or Genesis Plus GX wins.
			expect(upstream('picodrive', 'smsplus')).toBe('smsplus');
			expect(upstream('picodrive', 'genesis_plus_gx')).toBe('genesis_plus_gx');
			for (const saved of ['smsplus', 'genesis_plus_gx', null])
				expect(resolve('picodrive', saved), `${saved}`).toBe('picodrive');
			expect(resolve('picodrive', null, true)).toBe('segaMS');
			expect(resolve('genesis_plus_gx', 'picodrive')).toBe('genesis_plus_gx');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s keeps the operator-supplied cores, Yabause and DeSmuME non-threaded, and requires threads only for PPSSPP and DOSBox Pure (B9)',
		(file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			expect(source).toContain('const requiresThreads = ["ppsspp", "dosbox_pure"];');
			expect(threadOverride).toContain(
				'["genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())'
			);
			// The operator-supplied cores and DeSmuME skip the CDN; Yabause takes upstream's path.
			expect(cdnSkip).toContain(
				'["genesis_plus_gx", "picodrive", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "vice_x128", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())'
			);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins an explicit snes9x despite a saved alternate core (SNES B3)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			for (const saved of ['snes9x', 'bsnes', 'genesis_plus_gx', null])
				expect(resolve('snes9x', saved), `${saved}`).toBe('snes9x');
			// The control and settings group stays upstream's singleton snes group.
			expect(resolve('snes9x', null, true)).toBe('snes');
			expect(resolve('snes9x', 'bsnes', true)).toBe('snes');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s picks the Snes9x payload from the WebGL 2 setting and never a threaded one (SNES B3)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const report = 'cores/reports/snes9x.json';
			// Fresh settings: the pinned report has no defaultWebGL2, so legacy.
			expect((await downloadCore(source, 'snes9x')).requests).toEqual([
				report,
				'cores/snes9x-legacy-wasm.data'
			]);
			// A saved enabled WebGL 2 setting on a browser that supports it.
			expect((await downloadCore(source, 'snes9x', { webgl2Enabled: true })).requests).toEqual([
				report,
				'cores/snes9x-wasm.data'
			]);
			// Without WebGL 2 the constructor turns the setting off.
			expect(
				(await downloadCore(source, 'snes9x', { supportsWebgl2: false, webgl2Enabled: false }))
					.requests
			).toEqual([report, 'cores/snes9x-legacy-wasm.data']);
			// A saved "threads enabled" can't pick a threaded payload, which upstream would.
			const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
			expect(
				(await downloadCore(upstream, 'snes9x', { savedThreads: 'enabled' })).requests
			).toEqual([report, 'cores/snes9x-thread-legacy-wasm.data']);
			for (const webgl2Enabled of [null, true])
				expect(
					(await downloadCore(source, 'snes9x', { savedThreads: 'enabled', webgl2Enabled }))
						.requests[1]
				).not.toContain('-thread');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s still fetches the Snes9x payload after a failed report (SNES B3)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const run = await downloadCore(source, 'snes9x', { report: async () => -1 });
			expect(run.requests).toEqual(['cores/reports/snes9x.json', 'cores/snes9x-legacy-wasm.data']);
			expect(run.errors).toEqual([]);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s shows a local error for a failed Snes9x payload, without the CDN (SNES B3)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const run = await downloadCore(source, 'snes9x', { payload: async () => -1 });
			expect(run.requests).toEqual(['cores/reports/snes9x.json', 'cores/snes9x-legacy-wasm.data']);
			expect(run.errors).toEqual(['Error downloading core (snes9x-legacy-wasm.data)']);
			// Upstream would retry the payload from the EmulatorJS CDN.
			const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
			const fallback = await downloadCore(upstream, 'snes9x', { payload: async () => -1 });
			expect(fallback.requests[2]).toMatch(/^https:\/\/cdn\.emulatorjs\.org\//);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins an explicit opera, in the 3do group (3DO B6)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			for (const saved of ['opera', 'snes9x', null])
				expect(resolve('opera', saved), `${saved}`).toBe('opera');
			expect(resolve('opera', null, true)).toBe('3do');
			expect(resolve('opera', 'snes9x', true)).toBe('3do');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s never picks a threaded Opera payload (3DO B6)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const report = 'cores/reports/opera.json';
			expect((await downloadCore(source, 'opera')).requests).toEqual([
				report,
				'cores/opera-legacy-wasm.data'
			]);
			const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
			expect((await downloadCore(upstream, 'opera', { savedThreads: 'enabled' })).requests).toEqual(
				[report, 'cores/opera-thread-legacy-wasm.data']
			);
			for (const webgl2Enabled of [null, true])
				expect(
					(await downloadCore(source, 'opera', { savedThreads: 'enabled', webgl2Enabled }))
						.requests[1]
				).not.toContain('-thread');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s shows a local error for a failed Opera payload, without the CDN (3DO B6)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const run = await downloadCore(source, 'opera', { payload: async () => -1 });
			expect(run.requests).toEqual(['cores/reports/opera.json', 'cores/opera-legacy-wasm.data']);
			expect(run.errors).toEqual(['Error downloading core (opera-legacy-wasm.data)']);
			const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
			const fallback = await downloadCore(upstream, 'opera', { payload: async () => -1 });
			expect(fallback.requests[2]).toMatch(/^https:\/\/cdn\.emulatorjs\.org\//);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins an explicit vice_x64sc, in the c64 group (C64 B7)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			for (const saved of ['vice_x64sc', 'opera', null])
				expect(resolve('vice_x64sc', saved), `${saved}`).toBe('vice_x64sc');
			expect(resolve('vice_x64sc', null, true)).toBe('c64');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s never picks a threaded VICE payload, and shows a local error for a failed one (C64 B7)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const report = 'cores/reports/vice_x64sc.json';
			expect((await downloadCore(source, 'vice_x64sc')).requests).toEqual([
				report,
				'cores/vice_x64sc-legacy-wasm.data'
			]);
			for (const webgl2Enabled of [null, true])
				expect(
					(await downloadCore(source, 'vice_x64sc', { savedThreads: 'enabled', webgl2Enabled }))
						.requests[1]
				).not.toContain('-thread');
			const run = await downloadCore(source, 'vice_x64sc', { payload: async () => -1 });
			expect(run.errors).toEqual(['Error downloading core (vice_x64sc-legacy-wasm.data)']);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins an explicit vice_x128, in the c128 group',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			for (const saved of ['vice_x128', 'opera', null])
				expect(resolve('vice_x128', saved), `${saved}`).toBe('vice_x128');
			expect(resolve('vice_x128', null, true)).toBe('c128');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s never picks a threaded VICE (C128) payload, and shows a local error for a failed one',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			expect((await downloadCore(source, 'vice_x128')).requests).toEqual([
				'cores/reports/vice_x128.json',
				'cores/vice_x128-legacy-wasm.data'
			]);
			for (const webgl2Enabled of [null, true])
				expect(
					(await downloadCore(source, 'vice_x128', { savedThreads: 'enabled', webgl2Enabled }))
						.requests[1]
				).not.toContain('-thread');
			const run = await downloadCore(source, 'vice_x128', { payload: async () => -1 });
			expect(run.errors).toEqual(['Error downloading core (vice_x128-legacy-wasm.data)']);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins an explicit yabause, as upstream already does (Saturn B4)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			for (const saved of ['picodrive', 'mednafen_saturn', null]) {
				expect(upstream('yabause', saved), `${saved}`).toBe('yabause');
				expect(resolve('yabause', saved), `${saved}`).toBe('yabause');
			}
			expect(resolve('yabause', null, true)).toBe('segaSaturn');
		}
	);

	it('vendors exactly the pinned DeSmuME files (Nintendo DS B4)', () => {
		const pinned: Record<string, [number, string]> = {
			'desmume-wasm.data': [
				1172604,
				'a9fddaa4bd742e558dfe5095fa4eaf074493b591a7bc18c5f7c65d64b9fa7572'
			],
			'desmume-legacy-wasm.data': [
				1171393,
				'2af5d9642356b9ff8460a04ae5ab013afe34825abaded974a9b613dab73c1d4b'
			],
			'reports/desmume.json': [
				167,
				'd658e595994c6bf66acf3955d31eefe41e1418d58569c702fac80535262f02c2'
			]
		};
		expect(files(out).filter((f) => f.includes('desmume'))).toEqual(
			[
				...Object.keys(pinned).map((f) => `data/cores/${f}`),
				...coreLicenses.filter((f) => f.startsWith('desmume')).map((f) => `licenses/${f}`)
			].sort()
		);
		for (const [f, [size, sha256]] of Object.entries(pinned)) {
			const bytes = readFileSync(join(out, 'data', 'cores', f));
			expect(bytes.length, f).toBe(size);
			expect(createHash('sha256').update(bytes).digest('hex'), f).toBe(sha256);
		}
	});

	it('vendors exactly the pinned PUAE files (Amiga)', () => {
		const pinned: Record<string, [number, string]> = {
			'puae-wasm.data': [
				4140232,
				'8b58d776244c650c872dcaaf8693a2d75fd92a5219c7250cf5be5f517b11f1b5'
			],
			'puae-legacy-wasm.data': [
				4138844,
				'8d49f7b0bdb83992904a5f9a0d4b0c12e6e6b9b2e9bb619095b5ca6e6547279b'
			],
			'reports/puae.json': [141, 'a49ac77870e383d85894aaf791aa21bd104488a40e726c0b7aba8113a55094c8']
		};
		for (const [f, [size, sha256]] of Object.entries(pinned)) {
			const bytes = readFileSync(join(out, 'data', 'cores', f));
			expect(bytes.length, f).toBe(size);
			expect(createHash('sha256').update(bytes).digest('hex'), f).toBe(sha256);
		}
	});

	it('vendors exactly the pinned Beetle PCE files and no threaded payload (TurboGrafx-16 / PC Engine)', () => {
		const pinned: Record<string, [number, string]> = {
			'mednafen_pce-wasm.data': [
				994844,
				'29cebda0c7a93bbcb5e67e97fe28a1886bd030715d5a25224e7d9175d1d985c3'
			],
			'mednafen_pce-legacy-wasm.data': [
				995095,
				'c3d6e52680175b5528964b2c506a462bfd330d856661f28c5a54f7114b9b9a4c'
			],
			'reports/mednafen_pce.json': [
				223,
				'3c82092f1433cc2cf0d9265e137f44c5a80f5a7dfbfcb712ef659797299a67ca'
			]
		};
		expect(files(out).filter((f) => f.includes('mednafen_pce'))).toEqual(
			[
				...Object.keys(pinned).map((f) => `data/cores/${f}`),
				...coreLicenses.filter((f) => f.startsWith('mednafen_pce')).map((f) => `licenses/${f}`)
			].sort()
		);
		for (const [f, [size, sha256]] of Object.entries(pinned)) {
			const bytes = readFileSync(join(out, 'data', 'cores', f));
			expect(bytes.length, f).toBe(size);
			expect(createHash('sha256').update(bytes).digest('hex'), f).toBe(sha256);
		}
	});

	it('vendors exactly the pinned Beetle NeoPop files and no threaded payload (Neo Geo Pocket)', () => {
		const pinned: Record<string, [number, string]> = {
			'mednafen_ngp-wasm.data': [
				871904,
				'cdfe377bd380e418507dccda50d8664eecb06ebe1d2e5fbf5f397be859d1c83d'
			],
			'mednafen_ngp-legacy-wasm.data': [
				872066,
				'694089f04886d400b3672077ec9f32f408df26a8b204af6b4f3723197ea749c8'
			],
			'reports/mednafen_ngp.json': [
				126,
				'c47ef44f074a6b67f9f328e8c39bb846fce0ceb0ead1053f59c44360fd722162'
			]
		};
		expect(files(out).filter((f) => f.includes('mednafen_ngp'))).toEqual(
			[
				...Object.keys(pinned).map((f) => `data/cores/${f}`),
				...coreLicenses.filter((f) => f.startsWith('mednafen_ngp')).map((f) => `licenses/${f}`)
			].sort()
		);
		for (const [f, [size, sha256]] of Object.entries(pinned)) {
			const bytes = readFileSync(join(out, 'data', 'cores', f));
			expect(bytes.length, f).toBe(size);
			expect(createHash('sha256').update(bytes).digest('hex'), f).toBe(sha256);
		}
	});

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s pins desmume despite a saved melonDS and keeps the generic nds group (Nintendo DS B4)',
		(file) => {
			const resolve = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			// Upstream resolves desmume's nds group, so a saved melonDS wins.
			expect(upstream('desmume', 'melonds')).toBe('melonds');
			for (const saved of ['melonds', 'desmume2015', null])
				expect(resolve('desmume', saved), `${saved}`).toBe('desmume');
			expect(resolve('desmume', 'melonds', true)).toBe('nds');
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s picks a non-threaded DeSmuME payload despite saved melonDS and threads (Nintendo DS B4)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const report = 'cores/reports/desmume.json';
			const desmumeReport = async () => ({
				data: { buildStart: 1, options: { defaultWebGL2: true, supportsMouse: true } }
			});
			const saved = { savedCore: 'melonds', savedThreads: 'enabled', report: desmumeReport };
			// SharedArrayBuffer is defined in the sandbox; the pinned report turns WebGL 2 on by default.
			expect(typeof SharedArrayBuffer).toBe('function');
			expect((await downloadCore(source, 'desmume', saved)).requests).toEqual([
				report,
				'cores/desmume-wasm.data'
			]);
			expect(
				(
					await downloadCore(source, 'desmume', {
						...saved,
						supportsWebgl2: false,
						webgl2Enabled: false
					})
				).requests
			).toEqual([report, 'cores/desmume-legacy-wasm.data']);
			// Upstream would pick the threaded payload of the saved core.
			const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
			expect((await downloadCore(upstream, 'desmume', saved)).requests[0]).toBe(
				'cores/reports/melonds.json'
			);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s shows a local error for a missing DeSmuME payload, without the CDN (Nintendo DS B4)',
		async (file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const run = await downloadCore(source, 'desmume', { payload: async () => -1 });
			expect(run.requests).toEqual([
				'cores/reports/desmume.json',
				'cores/desmume-legacy-wasm.data'
			]);
			expect(run.errors).toEqual(['Error downloading core (desmume-legacy-wasm.data)']);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s shows the read error when a DeSmuME ROM read rejects (Nintendo DS B4)',
		async (file) => {
			const toData = emulatorMethod(readFileSync(join(out, 'data', file), 'utf8'), 'toData');
			const failing = new Blob(['nds']);
			failing.arrayBuffer = () => Promise.reject(new Error('read failed'));
			const errors: string[] = [];
			void toData.call(
				{
					getCore: () => 'desmume',
					localization: (t: string) => t,
					startGameError: (m: string) => errors.push(m)
				},
				failing
			);
			await tick();
			await tick();
			expect(errors).toEqual(['Error reading the game file']);
		}
	);

	it.each(['src/GameManager.js', 'emulator.min.js'])(
		'%s syncs the save folder in saveSaveFiles() for DeSmuME only (Nintendo DS B4)',
		(file) => {
			const emitted = readFileSync(join(out, 'data', 'src', 'GameManager.js'), 'utf8');
			const source = readFileSync(join(out, 'data', file), 'utf8');
			// The bundle carries the same edited GameManager.js.
			expect(source.split(syncfsEdit)).toHaveLength(2);
			expect(source).toContain(emitted);
			const manager = gameManagerPrototype(emitted);
			for (const [core, expected] of [
				['desmume', [false]],
				['gambatte', []]
			] as const) {
				const syncs: boolean[] = [];
				let saved = 0;
				manager.saveSaveFiles.call({
					functions: { saveSaveFiles: () => saved++ },
					getSaveFile: () => null,
					EJS: { callEvent: () => {}, getCore: () => core },
					FS: { syncfs: (populate: boolean) => syncs.push(populate) }
				});
				expect(saved, core).toBe(1);
				expect(syncs, core).toEqual(expected);
			}
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s never offers Genesis Plus GX a threaded payload or the CDN fallback (Genesis B5)',
		(file) => {
			const source = readFileSync(join(out, 'data', file), 'utf8');
			const select = source.indexOf(legacyAnchor);
			expect(source.lastIndexOf(threadOverride, select) + threadOverride.length).toBe(select);
			const cdn = source.indexOf(cdnAnchor);
			expect(source.indexOf(cdnAnchor + cdnSkip)).toBe(cdn);
			// The skip returns before the CDN URL is built.
			expect(source.indexOf('https://cdn.emulatorjs.org/', cdn)).toBeGreaterThan(
				cdn + (cdnAnchor + cdnSkip).length
			);
		}
	);

	it.each(['src/emulator.js', 'emulator.min.js'])(
		'%s resolves generic groups and other cores as upstream does',
		(file) => {
			const patched = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			const cases: [string, string | null, boolean][] = [
				['segaMS', 'genesis_plus_gx', false],
				['segaMD', 'picodrive', false],
				['segaMD', null, false],
				['nes', 'nestopia', false],
				['nes', null, false],
				['psx', 'mednafen_psx_hw', false],
				['psx', null, false],
				['fceumm', null, true],
				['pcsx_rearmed', null, true],
				['gearcoleco', null, false],
				['gearcoleco', null, true],
				['stella2014', 'gambatte', false],
				['gambatte', null, true]
			];
			for (const [system, saved, generic] of cases)
				expect(patched(system, saved, generic), `${system}/${saved}/${generic}`).toBe(
					upstream(system, saved, generic)
				);
			expect(patched('segaMS', 'genesis_plus_gx')).toBe('genesis_plus_gx');
		}
	);

	it('concatenates the src scripts in loader order, each ending in a newline', () => {
		const loader = readFileSync(join(pkg, 'loader.js'), 'utf8');
		expect(loaderOrder.map((f) => loader.indexOf(`"${f}"`))).toEqual(
			loaderOrder.map((f) => loader.indexOf(`"${f}"`)).toSorted((a, b) => a - b)
		);
		// The emitted (patched) emulator.js and GameManager.js, then the unchanged upstream scripts.
		const expected = loaderOrder
			.map(
				(f) =>
					readFileSync(join(f in guardedEdits ? join(out, 'data') : pkg, 'src', f), 'utf8') + '\n'
			)
			.join('');
		expect(readFileSync(join(out, 'data', 'emulator.min.js'), 'utf8')).toBe(expected);
	});

	it('copies the reviewed core notices and licence texts byte for byte', () => {
		expect(readdirSync('emulator-licenses').sort()).toEqual(coreLicenses);
		for (const f of coreLicenses)
			expect(readFileSync(join(out, 'licenses', f)), f).toEqual(
				readFileSync(join('emulator-licenses', f))
			);
	});

	it('copies the EmulatorJS licence', () => {
		expect(readFileSync(join(out, 'licenses', 'emulatorjs.txt'), 'utf8')).toBe(
			readFileSync(join('node_modules', '@emulatorjs', 'emulatorjs', 'LICENSE'), 'utf8')
		);
		expect(existsSync(join(out, 'data', 'localization'))).toBe(false);
	});

	it('copies the azahar payload and report byte for byte from the pinned cache (3DS V2)', () => {
		const pins = JSON.parse(readFileSync('emulatorjs-next.json', 'utf8')) as Record<
			'source' | 'core' | 'report',
			{ sha256: string }
		>;
		const cache = join('node_modules', '.cache', 'emulatorjs-next');
		for (const [file, pin] of [
			['azahar-thread-wasm.data', pins.core],
			['reports/azahar.json', pins.report]
		] as const) {
			const bytes = readFileSync(join(out, 'ejs-4.3', 'data', 'cores', file));
			expect(bytes.equals(readFileSync(join(cache, pin.sha256))), file).toBe(true);
			expect(createHash('sha256').update(bytes).digest('hex'), file).toBe(pin.sha256);
		}
		expect(readFileSync(join(out, 'licenses', 'emulatorjs-4.3.txt'), 'utf8')).toContain(
			'GNU GENERAL PUBLIC LICENSE'
		);
	});

	it('applies the one edits43 edit to the 4.3 emulator.js (3DS V3)', () => {
		const source = readFileSync(join(out, 'ejs-4.3', 'data', 'src', 'emulator.js'), 'utf8');
		expect(source).not.toContain('                const arrayBuffer = await url.arrayBuffer();\n');
		expect(source.split(fileReadGuard)).toHaveLength(2);
		// The 4.2.3 edits stay out of the 4.3 copy.
		expect(source).not.toContain(pinGuard);
		expect(source).not.toContain(threadOverride);
	});
});

describe('vendor-emulatorjs pins (3DS V2)', () => {
	let dir: string;

	beforeAll(() => {
		dir = mkdtempSync(join(tmpdir(), 'ejs-next-'));
	});

	afterAll(() => rmSync(dir, { recursive: true, force: true }));

	it('fails on a cached file whose SHA-256 differs from the pin, and deletes it, offline', () => {
		const bytes = Buffer.from('not the pinned tarball');
		const wrong = '0'.repeat(64);
		const cache = join(dir, 'cache');
		mkdirSync(cache);
		writeFileSync(join(cache, wrong), bytes);
		const url = 'https://example.invalid/emulatorjs.tar.gz';
		writeFileSync(
			join(dir, 'next.json'),
			JSON.stringify({ source: { url, sha256: wrong }, core: {}, report: {} })
		);
		let stderr = '';
		try {
			execFileSync(process.execPath, ['scripts/vendor-emulatorjs.mjs', join(dir, 'out')], {
				env: { ...process.env, EJS_NEXT_MANIFEST: join(dir, 'next.json'), EJS_NEXT_CACHE: cache },
				stdio: 'pipe'
			});
		} catch (e) {
			stderr = String((e as { stderr: Buffer }).stderr);
		}
		const actual = createHash('sha256').update(bytes).digest('hex');
		expect(stderr).toContain(`vendor-emulatorjs: ${url} has SHA-256 ${actual}, expected ${wrong}`);
		expect(existsSync(join(cache, wrong))).toBe(false);
	});
});
