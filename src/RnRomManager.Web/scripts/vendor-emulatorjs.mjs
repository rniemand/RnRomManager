// Browser emulators B9: copies the pinned EmulatorJS files the play page needs into static/emulator/vendor/
// (or the given outDir). Runs from predev/prebuild; a missing source file fails it.
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const outDir = resolve(web, process.argv[2] ?? 'static/emulator/vendor');
const pkgs = join(web, 'node_modules', '@emulatorjs');
const src = join(pkgs, 'emulatorjs', 'data');
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
	'desmume'
];

// Guarded edits of the pinned EmulatorJS scripts: each anchor must occur exactly once, or vendoring fails so the edit
// is reviewed against the new upstream text.
//
// Game Gear and Master System are pinned to SMS Plus GX, NES to FCEUmm, PlayStation to PCSX ReARMed, (Genesis B5)
// Mega Drive to Genesis Plus GX and (32X B3) 32X to PicoDrive: EmulatorJS 4.2.3's getCore() would otherwise switch to
// a core saved in the retroarch_core setting (genesis_plus_gx, picodrive, nestopia, mednafen_psx_hw, or smsplus for
// Genesis and PicoDrive, whose first group is segaMS), none of which is vendored or delivered for that system. An
// explicit pinned core returns itself before the saved setting is read. Saturn B4: yabause is listed too, defensively
// only: its segaSaturn group holds just yabause, so no other saved core could be chosen. SNES B3: snes9x likewise, as
// the only member of the snes group. 3DO B6: opera likewise, as the only member of the 3do group. C64 B7: vice_x64sc likewise, as the only member of the
// c64 group.
//
// PSP B3 (PPSSPP only): the core needs threads, so a saved "ejs_threads: disabled" must not select a payload that
// isn't vendored; and a failed ppsspp-assets.zip request or a rejected File read must show a startup error instead of
// leaving the loader's promises pending.
//
// Genesis B5, 32X B3, SNES B3, 3DO B6, C64 B7: the operator supplies only the non-threaded payloads, so Genesis Plus GX,
// PicoDrive, Snes9x, Opera and VICE never select a "-thread" one, and a payload that can't be fetched shows a local error instead of falling back to the
// EmulatorJS CDN. Saturn B9: only Yabause's non-threaded payloads are vendored, so it never selects a "-thread" one
// either (its missing payloads still take upstream's path).
//
// Nintendo DS B4: desmume joins the pinned cores (the generic nds settings key stays, so a saved retroarch_core such as
// melonds is ignored), never selects a "-thread" payload, shows a local error for a missing payload instead of trying
// the CDN, and shows "Error reading the game file" for a rejected ROM read. DeSmuME writes its own open .dsv into
// /data/saves, which EmulatorJS 4.2.3's IDBFS persists only when a file closes, and saveSaveFiles() never syncs (the
// call is commented out), so for desmume only it syncs, and the 30-second save interval persists the battery save.
const edits = [
	{
		file: 'emulator.js',
		find:
			'        const gen = this.getCore(true);\n' +
			'        if (cores[gen] && cores[gen].includes(this.preGetSetting("retroarch_core"))) {',
		insertBefore:
			'        if (["smsplus", "fceumm", "pcsx_rearmed", "genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc"].includes(core)) {\n' +
			'            return core;\n' +
			'        }\n'
	},
	{
		file: 'emulator.js',
		find: '            let legacy = (this.supportsWebgl2 && this.webgl2Enabled ? "" : "-legacy");\n',
		insertBefore:
			'            if (this.getCore() === "ppsspp" && typeof window.SharedArrayBuffer === "function") {\n' +
			'                threads = true;\n' +
			'            }\n' +
			'            if (["genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc"].includes(this.getCore())) {\n' +
			'                threads = false;\n' +
			'            }\n'
	},
	{
		file: 'emulator.js',
		find:
			'            if (res === -1) {\n' +
			'                console.log("File not found, attemping to fetch from emulatorjs cdn.");\n',
		insertAfter:
			'                if (["genesis_plus_gx", "picodrive", "snes9x", "desmume", "opera", "vice_x64sc"].includes(this.getCore())) {\n' +
			'                    this.startGameError(this.localization("Error downloading core") + " (" + filename + ")");\n' +
			'                    return;\n' +
			'                }\n'
	},
	{
		file: 'emulator.js',
		find: '                resolve(new Uint8Array(await data.arrayBuffer()));\n',
		replace:
			'                let bytes;\n' +
			'                try {\n' +
			'                    bytes = await data.arrayBuffer();\n' +
			'                } catch (e) {\n' +
			'                    if (!["ppsspp", "desmume"].includes(this.getCore())) throw e;\n' +
			'                    this.startGameError(this.localization("Error reading the game file"));\n' +
			'                    return;\n' +
			'                }\n' +
			'                resolve(new Uint8Array(bytes));\n'
	},
	{
		file: 'GameManager.js',
		find: '            this.EJS.downloadFile("cores/ppsspp-assets.zip", null, false, { responseType: "arraybuffer", method: "GET" }).then((res) => {\n',
		insertAfter:
			'                if (res === -1) {\n' +
			'                    this.EJS.startGameError(this.EJS.localization("Error downloading core") + " (ppsspp-assets.zip)");\n' +
			'                    return;\n' +
			'                }\n'
	},
	{
		file: 'GameManager.js',
		find:
			'                        this.EJS.textElem.innerText = this.localization("Network Error");\n' +
			'                        this.EJS.textElem.style.color = "red";\n',
		replace:
			'                        this.EJS.startGameError(this.EJS.localization("Error downloading core") + " (ppsspp-assets.zip)");\n'
	},
	{
		file: 'GameManager.js',
		find: '        //this.FS.syncfs(false, () => {});\n',
		replace:
			'        if (this.EJS.getCore() === "desmume") {\n' +
			'            this.FS.syncfs(false, (err) => { if (err) console.warn(err); });\n' +
			'        }\n'
	}
];

function applyEdits(file, source) {
	for (const edit of edits.filter((e) => e.file === file)) {
		const at = source.indexOf(edit.find);
		if (at < 0 || source.indexOf(edit.find, at + 1) >= 0)
			throw new Error(
				`vendor-emulatorjs: ${file} changed; review the guarded edit at ${JSON.stringify(edit.find)}`
			);
		const end = at + edit.find.length;
		source =
			edit.replace !== undefined
				? source.slice(0, at) + edit.replace + source.slice(end)
				: edit.insertBefore !== undefined
					? source.slice(0, at) + edit.insertBefore + source.slice(at)
					: source.slice(0, end) + edit.insertAfter + source.slice(end);
	}
	return source;
}

function copy(from, to) {
	mkdirSync(dirname(to), { recursive: true });
	copyFileSync(from, to); // throws ENOENT on a missing source
}

rmSync(outDir, { recursive: true, force: true });
const data = join(outDir, 'data');

for (const file of ['loader.js', 'emulator.css', 'version.json'])
	copy(join(src, file), join(data, file));
const scripts = new Map();
for (const file of readdirSync(join(src, 'src'))) {
	const text = applyEdits(file, readFileSync(join(src, 'src', file), 'utf8'));
	scripts.set(file, text);
	mkdirSync(join(data, 'src'), { recursive: true });
	writeFileSync(join(data, 'src', file), text);
}
for (const file of readdirSync(join(src, 'compression')).filter((f) => f !== 'README.md'))
	copy(join(src, 'compression', file), join(data, 'compression', file));

for (const core of cores) {
	const pkg = join(pkgs, `core-${core}`);
	for (const variant of ['legacy-wasm', 'wasm'])
		copy(join(pkg, `${core}-${variant}.data`), join(data, 'cores', `${core}-${variant}.data`));
	copy(join(pkg, 'reports', `${core}.json`), join(data, 'cores', 'reports', `${core}.json`));
}
// PSP B3: PPSSPP has only a threaded WebGL 2 payload, plus the asset archive GameManager.js loads.
{
	const pkg = join(pkgs, 'core-ppsspp');
	for (const file of ['ppsspp-thread-wasm.data', 'ppsspp-assets.zip'])
		copy(join(pkg, file), join(data, 'cores', file));
	copy(join(pkg, 'reports', 'ppsspp.json'), join(data, 'cores', 'reports', 'ppsspp.json'));
}

// The package ships no minified build; loader.js tries emulator.min.js first, so build it the way loader.js's
// fallback loads the scripts: in its order, each ending in "\n" (socket.io.min.js ends in a // comment).
const order = [
	...readFileSync(join(src, 'loader.js'), 'utf8').matchAll(/const scripts = \[([^\]]*)\]/g)
].flatMap((m) => [...m[1].matchAll(/"([^"]+\.js)"/g)].map((s) => s[1]));
if (order.length === 0) throw new Error('vendor-emulatorjs: no script list found in loader.js');
writeFileSync(
	join(data, 'emulator.min.js'),
	order
		.map((f) => {
			if (!scripts.has(f))
				throw new Error(`vendor-emulatorjs: loader.js lists a missing script ${f}`);
			return scripts.get(f) + '\n';
		})
		.join('')
);
copy(join(src, 'emulator.css'), join(data, 'emulator.min.css'));

copy(join(pkgs, 'emulatorjs', 'LICENSE'), join(outDir, 'licenses', 'emulatorjs.txt'));
// Reviewed notices, licence texts and source records for cores whose packages ship none (emulator-licenses/).
for (const file of readdirSync(join(web, 'emulator-licenses')))
	copy(join(web, 'emulator-licenses', file), join(outDir, 'licenses', file));
console.log(`vendor-emulatorjs: wrote ${outDir}`);
