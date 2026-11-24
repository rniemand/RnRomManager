// Browser emulators B9: copies the pinned EmulatorJS files the play page needs into static/emulator/vendor/
// (or the given outDir). Runs from predev/prebuild; a missing source file fails it.
import { createHash } from 'node:crypto';
import {
	copyFileSync,
	existsSync,
	mkdirSync,
	readFileSync,
	readdirSync,
	renameSync,
	rmSync,
	writeFileSync
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

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
	'desmume',
	'puae',
	'mednafen_pce',
	'mednafen_ngp'
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
// c64 group, vice_xpet of the pet group and vice_xvic of the vic20 group. N64: mupen64plus_next is pinned for real, since
// the n64 group also holds parallel_n64 (EmulatorJS's default on mobile Safari), which isn't delivered. Arcade P1:
// mame2003_plus is pinned for real too, since the mame group also holds mame2003, which isn't delivered. Arcade (FinalBurn
// Neo) P1: fbneo likewise, since the arcade group also holds fbalpha2012_cps1 and fbalpha2012_cps2.
//
// PSP B3 (PPSSPP only): the core needs threads, so a saved "ejs_threads: disabled" must not select a payload that
// isn't vendored; and a failed ppsspp-assets.zip request or a rejected File read must show a startup error instead of
// leaving the loader's promises pending.
//
// Genesis B5, 32X B3, SNES B3, 3DO B6, C64 B7, PET, VIC-20, N64, Arcade P1: the operator supplies only the non-threaded payloads, so Genesis Plus GX,
// PicoDrive, Snes9x, Opera, VICE (x64sc, xpet and xvic), Mupen64Plus-Next and MAME 2003-Plus never select a "-thread" one, and a payload that can't be fetched shows a local error instead of falling back to the
// EmulatorJS CDN. Saturn B9: only Yabause's non-threaded payloads are vendored, so it never selects a "-thread" one
// either (its missing payloads still take upstream's path).
//
// Nintendo DS B4: desmume joins the pinned cores (the generic nds settings key stays, so a saved retroarch_core such as
// melonds is ignored), never selects a "-thread" payload, shows a local error for a missing payload instead of trying
// the CDN, and shows "Error reading the game file" for a rejected ROM read. DeSmuME writes its own open .dsv into
// /data/saves, which EmulatorJS 4.2.3's IDBFS persists only when a file closes, and saveSaveFiles() never syncs (the
// call is commented out), so for desmume only it syncs, and the 30-second save interval persists the battery save.
//
// MS-DOS P2: DOSBox Pure requires threads, so it gets the threaded payload as PPSSPP does, and a rejected File read
// shows a startup error. Its game ZIP is written to the content name whole, skipping checkCompression: unpacked,
// EmulatorJS would boot the ZIP's first supported file (often INSTALL.BAT or SETUP.EXE) instead of DOSBox Pure's
// start menu for the mounted ZIP. It is the only core in EmulatorJS's dos group, so getCore() needs no pin.
//
// Saturn and Sega CD multi-disc S4: downloadRom() prefers an .m3u only for the psx group; for Yabause and Genesis Plus
// GX an .iso/.chd or a .cue among the unpacked files would win. A play-disc-set ZIP carries the set's .m3u, so for those
// two cores an unpacked .m3u is the content and the image/cue choice is skipped. Single-disc ZIPs hold no .m3u, so they
// (and every other core) take upstream's choice.
const edits = [
	{
		file: 'emulator.js',
		find:
			'        const gen = this.getCore(true);\n' +
			'        if (cores[gen] && cores[gen].includes(this.preGetSetting("retroarch_core"))) {',
		insertBefore:
			'        if (["smsplus", "fceumm", "pcsx_rearmed", "genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(core)) {\n' +
			'            return core;\n' +
			'        }\n'
	},
	{
		file: 'emulator.js',
		find: '            let legacy = (this.supportsWebgl2 && this.webgl2Enabled ? "" : "-legacy");\n',
		insertBefore:
			'            if (["ppsspp", "dosbox_pure"].includes(this.getCore()) && typeof window.SharedArrayBuffer === "function") {\n' +
			'                threads = true;\n' +
			'            }\n' +
			'            if (["genesis_plus_gx", "picodrive", "yabause", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())) {\n' +
			'                threads = false;\n' +
			'            }\n'
	},
	{
		file: 'emulator.js',
		find:
			'            if (res === -1) {\n' +
			'                console.log("File not found, attemping to fetch from emulatorjs cdn.");\n',
		insertAfter:
			'                if (["genesis_plus_gx", "picodrive", "snes9x", "desmume", "opera", "vice_x64sc", "vice_xpet", "vice_xvic", "mupen64plus_next", "mame2003_plus", "fbneo"].includes(this.getCore())) {\n' +
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
			'                    if (!["ppsspp", "desmume", "dosbox_pure"].includes(this.getCore())) throw e;\n' +
			'                    this.startGameError(this.localization("Error reading the game file"));\n' +
			'                    return;\n' +
			'                }\n' +
			'                resolve(new Uint8Array(bytes));\n'
	},
	{
		file: 'emulator.js',
		find: '                    if (isoFile !== null && (supportsExt("iso") || supportsExt("cso") || supportsExt("chd") || supportsExt("elf"))) {\n',
		replace:
			'                    const m3uFile = fileNames.find((name) => name.split(".").pop().toLowerCase() === "m3u");\n' +
			'                    if (m3uFile !== undefined && ["yabause", "genesis_plus_gx"].includes(this.getCore())) {\n' +
			'                        this.fileName = m3uFile;\n' +
			'                    } else if (isoFile !== null && (supportsExt("iso") || supportsExt("cso") || supportsExt("chd") || supportsExt("elf"))) {\n'
	},
	{
		file: 'emulator.js',
		find: '                const altName = this.getBaseFileName(true);\n',
		insertAfter:
			'                if (this.getCore() === "dosbox_pure") {\n' +
			'                    this.gameManager.FS.writeFile(altName, new Uint8Array(data));\n' +
			'                    this.fileName = altName;\n' +
			'                    resolve();\n' +
			'                    return;\n' +
			'                }\n'
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

// 3DS V3: the one guarded edit of the EmulatorJS 4.3.0-pre copy, which serves only azahar: a File ROM the browser
// can't buffer (a multi-GiB dump) shows a startup error instead of leaving download() pending.
const edits43 = [
	{
		file: 'src/emulator.js',
		find: '                const arrayBuffer = await url.arrayBuffer();\n',
		replace:
			'                let arrayBuffer;\n' +
			'                try {\n' +
			'                    arrayBuffer = await url.arrayBuffer();\n' +
			'                } catch (e) {\n' +
			'                    this.startGameError(this.localization("Error reading the game file"));\n' +
			'                    return;\n' +
			'                }\n'
	}
];

function applyEdits(file, source, list = edits) {
	for (const edit of list.filter((e) => e.file === file)) {
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
// MS-DOS P1: DOSBox Pure ships threaded payloads only (WebGL 2 and legacy).
{
	const pkg = join(pkgs, 'core-dosbox_pure');
	for (const file of ['dosbox_pure-thread-wasm.data', 'dosbox_pure-thread-legacy-wasm.data'])
		copy(join(pkg, file), join(data, 'cores', file));
	copy(
		join(pkg, 'reports', 'dosbox_pure.json'),
		join(data, 'cores', 'reports', 'dosbox_pure.json')
	);
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

// 3DS V1–V2: EmulatorJS 4.3.0-pre's scripts and its azahar core, side by side under ejs-4.3/ for play-3ds.html only.
// Each pinned download is cached by its SHA-256 and verified on every run.
const nextManifest = JSON.parse(
	readFileSync(process.env.EJS_NEXT_MANIFEST ?? join(web, 'emulatorjs-next.json'), 'utf8')
);
const nextCache = resolve(
	process.env.EJS_NEXT_CACHE ?? join(web, 'node_modules', '.cache', 'emulatorjs-next')
);

async function pinned({ url, sha256 }) {
	const path = join(nextCache, sha256);
	if (!existsSync(path)) {
		let res;
		try {
			res = await fetch(url);
		} catch (e) {
			throw new Error(`vendor-emulatorjs: couldn't download ${url} (${e.message})`, { cause: e });
		}
		if (!res.ok) throw new Error(`vendor-emulatorjs: ${url} returned HTTP ${res.status}`);
		mkdirSync(nextCache, { recursive: true });
		// The pid keeps two concurrent runs (npm run build and vitest) from writing the same temporary file.
		const tmp = `${path}.${process.pid}.tmp`;
		writeFileSync(tmp, Buffer.from(await res.arrayBuffer()));
		renameSync(tmp, path);
	}
	const bytes = readFileSync(path);
	const actual = createHash('sha256').update(bytes).digest('hex');
	if (actual !== sha256) {
		rmSync(path, { force: true });
		throw new Error(`vendor-emulatorjs: ${url} has SHA-256 ${actual}, expected ${sha256}`);
	}
	return bytes;
}

/** The regular files of a .tar.gz (ustar, GitHub's pax header skipped), keyed by path without the top folder. */
function untar(tgz) {
	const tar = gunzipSync(tgz);
	const entries = new Map();
	for (let at = 0; at + 512 <= tar.length;) {
		const header = tar.subarray(at, at + 512);
		if (header.every((b) => b === 0)) break;
		const field = (offset, length) => {
			const bytes = header.subarray(offset, offset + length);
			const nul = bytes.indexOf(0);
			return bytes.subarray(0, nul < 0 ? length : nul).toString('utf8');
		};
		const size = parseInt(field(124, 12).trim() || '0', 8);
		const prefix = field(345, 155);
		const name = (prefix ? `${prefix}/` : '') + field(0, 100);
		if (['0', ''].includes(field(156, 1)))
			entries.set(name.slice(name.indexOf('/') + 1), tar.subarray(at + 512, at + 512 + size));
		at += 512 + Math.ceil(size / 512) * 512;
	}
	return entries;
}

const next = join(outDir, 'ejs-4.3', 'data');
const tarball = untar(await pinned(nextManifest.source));
const nextFiles = [
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
for (const file of nextFiles) {
	const bytes = tarball.get(`data/${file}`);
	if (bytes === undefined)
		throw new Error(`vendor-emulatorjs: data/${file} is missing from ${nextManifest.source.url}`);
	mkdirSync(dirname(join(next, file)), { recursive: true });
	writeFileSync(
		join(next, file),
		file.startsWith('src/') ? applyEdits(file, bytes.toString('utf8'), edits43) : bytes
	);
}
writeFileSync(join(outDir, 'licenses', 'emulatorjs-4.3.txt'), tarball.get('LICENSE'));
mkdirSync(join(next, 'cores', 'reports'), { recursive: true });
writeFileSync(join(next, 'cores', 'azahar-thread-wasm.data'), await pinned(nextManifest.core));
writeFileSync(join(next, 'cores', 'reports', 'azahar.json'), await pinned(nextManifest.report));
console.log(`vendor-emulatorjs: wrote ${outDir}`);
