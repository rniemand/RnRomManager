import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative, sep } from 'node:path';
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
		const cores = ['gambatte', 'mgba'].flatMap((c) => [
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
				'licenses/emulatorjs.txt'
			].sort()
		);
		expect(files(out).some((f) => f.includes('-thread'))).toBe(false);
	});

	it('concatenates the src scripts in loader order, each ending in a newline', () => {
		const loader = readFileSync(join(pkg, 'loader.js'), 'utf8');
		expect(loaderOrder.map((f) => loader.indexOf(`"${f}"`))).toEqual(
			loaderOrder.map((f) => loader.indexOf(`"${f}"`)).toSorted((a, b) => a - b)
		);
		const expected = loaderOrder
			.map((f) => readFileSync(join(pkg, 'src', f), 'utf8') + '\n')
			.join('');
		expect(readFileSync(join(out, 'data', 'emulator.min.js'), 'utf8')).toBe(expected);
	});

	it('copies the EmulatorJS licence', () => {
		expect(readFileSync(join(out, 'licenses', 'emulatorjs.txt'), 'utf8')).toBe(
			readFileSync(join('node_modules', '@emulatorjs', 'emulatorjs', 'LICENSE'), 'utf8')
		);
		expect(existsSync(join(out, 'data', 'localization'))).toBe(false);
	});
});
