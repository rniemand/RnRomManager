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

// The reviewed notices and licence texts in emulator-licenses/ (step 6 of the 2600/Coleco/GG spec).
const coreLicenses = [
	'COMMON-BUILD.txt',
	'gearcoleco-LICENSE.txt',
	'gearcoleco-NOTICE.txt',
	'retroarch-LICENSE.txt',
	'smsplus-LICENSE.txt',
	'smsplus-NOTICE.txt',
	'smsplus-sms_ntsc-LICENSE.txt',
	'stella2014-LICENSE.txt',
	'stella2014-NOTICE.txt'
];

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
			'smsplus'
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
				'licenses/emulatorjs.txt',
				...coreLicenses.map((f) => `licenses/${f}`)
			].sort()
		);
		expect(files(out).some((f) => f.includes('-thread'))).toBe(false);
	});

	it('copies the src scripts unchanged except the guarded getCore() edit in emulator.js', () => {
		for (const f of loaderOrder.filter((f) => f !== 'emulator.js'))
			expect(readFileSync(join(out, 'data', 'src', f), 'utf8')).toBe(
				readFileSync(join(pkg, 'src', f), 'utf8')
			);
		const upstream = readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8');
		const anchor = '        const gen = this.getCore(true);\n';
		const guard = '        if (core === "smsplus") {\n            return core;\n        }\n';
		expect(upstream.split(anchor)).toHaveLength(2);
		expect(readFileSync(join(out, 'data', 'src', 'emulator.js'), 'utf8')).toBe(
			upstream.replace(anchor, guard + anchor)
		);
	});

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
		'%s resolves generic groups and other cores as upstream does',
		(file) => {
			const patched = coreResolver(readFileSync(join(out, 'data', file), 'utf8'));
			const upstream = coreResolver(readFileSync(join(pkg, 'src', 'emulator.js'), 'utf8'));
			const cases: [string, string | null, boolean][] = [
				['segaMS', 'genesis_plus_gx', false],
				['segaMD', 'picodrive', false],
				['segaMD', null, false],
				['nes', 'nestopia', false],
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
		// The emitted (patched) emulator.js, then the unchanged upstream scripts.
		const expected = loaderOrder
			.map(
				(f) =>
					readFileSync(join(f === 'emulator.js' ? join(out, 'data') : pkg, 'src', f), 'utf8') + '\n'
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
