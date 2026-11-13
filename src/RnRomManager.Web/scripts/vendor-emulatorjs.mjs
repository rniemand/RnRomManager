// Browser emulators B9: copies the pinned EmulatorJS files the play page needs into static/emulator/vendor/
// (or the given outDir). Runs from predev/prebuild; a missing source file fails it.
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const outDir = resolve(web, process.argv[2] ?? 'static/emulator/vendor');
const pkgs = join(web, 'node_modules', '@emulatorjs');
const src = join(pkgs, 'emulatorjs', 'data');
const cores = ['gambatte', 'mgba', 'handy', 'virtualjaguar'];

function copy(from, to) {
	mkdirSync(dirname(to), { recursive: true });
	copyFileSync(from, to); // throws ENOENT on a missing source
}

rmSync(outDir, { recursive: true, force: true });
const data = join(outDir, 'data');

for (const file of ['loader.js', 'emulator.css', 'version.json'])
	copy(join(src, file), join(data, file));
for (const file of readdirSync(join(src, 'src')))
	copy(join(src, 'src', file), join(data, 'src', file));
for (const file of readdirSync(join(src, 'compression')).filter((f) => f !== 'README.md'))
	copy(join(src, 'compression', file), join(data, 'compression', file));

for (const core of cores) {
	const pkg = join(pkgs, `core-${core}`);
	for (const variant of ['legacy-wasm', 'wasm'])
		copy(join(pkg, `${core}-${variant}.data`), join(data, 'cores', `${core}-${variant}.data`));
	copy(join(pkg, 'reports', `${core}.json`), join(data, 'cores', 'reports', `${core}.json`));
}

// The package ships no minified build; loader.js tries emulator.min.js first, so build it the way loader.js's
// fallback loads the scripts: in its order, each ending in "\n" (socket.io.min.js ends in a // comment).
const order = [
	...readFileSync(join(src, 'loader.js'), 'utf8').matchAll(/const scripts = \[([^\]]*)\]/g)
].flatMap((m) => [...m[1].matchAll(/"([^"]+\.js)"/g)].map((s) => s[1]));
if (order.length === 0) throw new Error('vendor-emulatorjs: no script list found in loader.js');
writeFileSync(
	join(data, 'emulator.min.js'),
	order.map((f) => readFileSync(join(src, 'src', f), 'utf8') + '\n').join('')
);
copy(join(src, 'emulator.css'), join(data, 'emulator.min.css'));

copy(join(pkgs, 'emulatorjs', 'LICENSE'), join(outDir, 'licenses', 'emulatorjs.txt'));
console.log(`vendor-emulatorjs: wrote ${outDir}`);
