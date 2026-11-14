import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
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
	'fceumm-LICENSE.txt',
	'fceumm-NOTICE.txt',
	'gearcoleco-LICENSE.txt',
	'gearcoleco-NOTICE.txt',
	'pcsx_rearmed-LICENSE.txt',
	'pcsx_rearmed-NOTICE.txt',
	'pcsx_rearmed-libchdr-LICENSE.txt',
	'pcsx_rearmed-zstd-LICENSE.txt',
	'ppsspp-LICENSE.txt',
	'ppsspp-NOTICE.txt',
	'retroarch-LICENSE.txt',
	'smsplus-LICENSE.txt',
	'smsplus-NOTICE.txt',
	'smsplus-sms_ntsc-LICENSE.txt',
	'stella2014-LICENSE.txt',
	'stella2014-NOTICE.txt'
];

// The guarded vendoring edits (anchor → its replacement), applied in order.
const getCoreAnchor = '        const gen = this.getCore(true);\n';
const pinGuard =
	'        if (["smsplus", "fceumm", "pcsx_rearmed", "genesis_plus_gx"].includes(core)) {\n' +
	'            return core;\n' +
	'        }\n';
const legacyAnchor =
	'            let legacy = (this.supportsWebgl2 && this.webgl2Enabled ? "" : "-legacy");\n';
const threadOverride =
	'            if (this.getCore() === "ppsspp" && typeof window.SharedArrayBuffer === "function") {\n' +
	'                threads = true;\n' +
	'            }\n' +
	'            if (this.getCore() === "genesis_plus_gx") {\n' +
	'                threads = false;\n' +
	'            }\n';
const cdnAnchor =
	'            if (res === -1) {\n' +
	'                console.log("File not found, attemping to fetch from emulatorjs cdn.");\n';
const cdnSkip =
	'                if (this.getCore() === "genesis_plus_gx") {\n' +
	'                    this.startGameError(this.localization("Error downloading core") + " (" + filename + ")");\n' +
	'                    return;\n' +
	'                }\n';
const assetsRequest =
	'            this.EJS.downloadFile("cores/ppsspp-assets.zip", null, false, { responseType: "arraybuffer", method: "GET" }).then((res) => {\n';
const assetsError =
	'this.EJS.startGameError(this.EJS.localization("Error downloading core") + " (ppsspp-assets.zip)");\n';
const guardedEdits: Record<string, [string, string][]> = {
	'emulator.js': [
		[getCoreAnchor, pinGuard + getCoreAnchor],
		[legacyAnchor, threadOverride + legacyAnchor],
		[cdnAnchor, cdnAnchor + cdnSkip],
		[
			'                resolve(new Uint8Array(await data.arrayBuffer()));\n',
			'                let bytes;\n' +
				'                try {\n' +
				'                    bytes = await data.arrayBuffer();\n' +
				'                } catch (e) {\n' +
				'                    if (this.getCore() !== "ppsspp") throw e;\n' +
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
		]
	]
};

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

/** EJS_GameManager's prototype from GameManager.js, run outside a browser. */
function gameManagerPrototype(source: string): {
	loadPpssppAssets: (this: object) => Promise<void>;
} {
	const window: Record<
		string,
		{ prototype: { loadPpssppAssets: (this: object) => Promise<void> } }
	> = {};
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
			'pcsx_rearmed'
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
				'licenses/emulatorjs.txt',
				...coreLicenses.map((f) => `licenses/${f}`)
			].sort()
		);
		// PSP B3: PPSSPP's threaded payload is the only one; every other core is single-threaded.
		expect(files(out).filter((f) => f.includes('-thread'))).toEqual([
			'data/cores/ppsspp-thread-wasm.data'
		]);
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
		'%s forces the threaded payload for PPSSPP only, despite a saved ejs_threads setting',
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
});
