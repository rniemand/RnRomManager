import { api } from './api/client';
import type { GameDetail, RomDetail } from './api/types';

/**
 * Play B1: the cores that play a release stored outside the ZIP through play-disc: a PlayStation disc, (PSP B2) a
 * PlayStation Portable UMD image, (Saturn B6) a Saturn disc and (Sega CD B5) a Sega CD disc on Genesis Plus GX, which
 * also plays Mega Drive cartridges from the ZIP: the play URL's `disc` flag tells the two apart. 3DO B7: Opera plays
 * only discs. TurboGrafx-CD B5: Beetle PCE plays TurboGrafx-CD discs and, from the ZIP, TurboGrafx-16 HuCards. MS-DOS
 * P3: DOSBox Pure plays an external game ZIP (`.dosz`), fetched from `/download`. 3DS P5: azahar plays an external
 * `.3ds`/`.cci` from `/download`. Arcade P5: MAME 2003-Plus plays an external set ZIP (`.mamez`) from `/download`, its parent and
 * BIOS sets beside it.
 */
export const externalBrowserCores: readonly string[] = [
	'pcsx_rearmed',
	'ppsspp',
	'yabause',
	'genesis_plus_gx',
	'opera',
	'desmume',
	'mednafen_pce',
	'dosbox_pure',
	'azahar',
	'mame2003_plus',
	'fbneo',
	'mednafen_pcfx'
];

/** S8: the cores whose external releases load through play-disc; DeSmuME loads an external DS ROM from `/download`. */
export const playDiscCores: readonly string[] = [
	'pcsx_rearmed',
	'ppsspp',
	'yabause',
	'genesis_plus_gx',
	'opera',
	'mednafen_pce',
	'mednafen_pcfx'
];

/** S8: whether the play URL carries `disc=1`: an external release of a play-disc core. */
export function usesPlayDisc(core: string, rom: Pick<RomDetail, 'externalFileName'>): boolean {
	return rom.externalFileName !== null && playDiscCores.includes(core);
}

/** Amiga F8: what a set's All button plays: floppy disks for PUAE, discs for every other core. */
export function setMedia(core: string | null): 'disks' | 'discs' {
	return core === 'puae' ? 'disks' : 'discs';
}

/** PSP B3, MS-DOS P5, 3DS G1: the cores that play only in their own cross-origin isolated tab, and that tab's page. */
export const isolatedPages: Readonly<Record<string, string>> = {
	ppsspp: 'play-psp.html',
	dosbox_pure: 'play-dos.html',
	azahar: 'play-3ds.html'
};

/** PSP B3, MS-DOS P5: whether Play opens the core in its own tab instead of the modal. */
export function opensInNewTab(core: string | null): boolean {
	return core !== null && Object.hasOwn(isolatedPages, core);
}

/**
 * Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. Play B1: a PlayStation
 * or PSP release is external and plays anyway, and (Saturn B6) a Saturn bin/cue disc; other external releases (Switch,
 * GameCube…) don't. Browser play format gaps N5: a Saturn .nrg plays (play-disc sends a generated cue). CHD B5: a standalone .chd plays like an .iso (PlayStation, Saturn, Sega CD).
 * TurboGrafx-CD B5: Beetle PCE runs a lone .bin as a HuCard, so on `mednafen_pce` an external .bin plays only with its
 * set files (the cue); the API's quick play applies the same rule. PC-FX X6: Beetle PC-FX can't open a lone .bin
 * either, so `mednafen_pcfx` follows the same rule. Jaguar: see `jaguarRomLoads`. Amiga: PUAE's
 * WebAssembly build has no capsimg, so an .ipf never plays (the API's quick play agrees). 3DS P3: see `azaharRomLoads`.
 */
export function canPlay(
	game: Pick<GameDetail, 'browserCore'>,
	rom: Pick<RomDetail, 'externalFileName'> &
		Partial<Pick<RomDetail, 'files' | 'fileName' | 'sizeBytes' | 'header'>>
): boolean {
	const external = rom.externalFileName?.toLowerCase() ?? null;
	return (
		game.browserCore !== null &&
		(external === null || externalBrowserCores.includes(game.browserCore)) &&
		!(
			(game.browserCore === 'mednafen_pce' || game.browserCore === 'mednafen_pcfx') &&
			(external?.endsWith('.bin') ?? false) &&
			!rom.files?.length
		) &&
		!(game.browserCore === 'puae' && (rom.fileName?.toLowerCase().endsWith('.ipf') ?? false)) &&
		!(
			game.browserCore === 'virtualjaguar' &&
			rom.fileName !== undefined &&
			rom.sizeBytes !== undefined &&
			!jaguarRomLoads(rom.fileName, rom.sizeBytes)
		) &&
		!(
			game.browserCore === 'azahar' &&
			rom.fileName !== undefined &&
			!azaharRomLoads(rom.fileName, rom.header ?? null)
		)
	);
}

/**
 * Virtual Jaguar plays a file only from cart space. An .abs, .cof or .jag program loads into RAM, which this EmulatorJS
 * build's reset wipes after loading (a black screen). A .j64/.rom is placed by size alone: a cart (a whole number of
 * MiB, or 128 KiB) or an Alpine image (8 KiB short of one); any other size loads nothing. The API's quick play applies
 * the same rule (`LibraryEndpoints.JaguarRomLoads`).
 */
export function jaguarRomLoads(fileName: string, sizeBytes: number): boolean {
	if (/\.(abs|cof|jag)$/i.test(fileName)) return false;
	const mib = 1048576;
	return sizeBytes % mib === 0 || sizeBytes === 131072 || (sizeBytes + 8192) % mib === 0;
}

/**
 * 3DS P2: azahar plays a `.3ds` or `.cci` whose header doesn't say it's encrypted (a missing header or field counts as
 * playable); its libretro build can't load a `.cia`. The API's quick play applies the same rule
 * (`LibraryEndpoints.AzaharRomLoads`).
 */
export function azaharRomLoads(fileName: string, header: Record<string, unknown> | null): boolean {
	return /\.(3ds|cci)$/i.test(fileName) && header?.encrypted !== true;
}

/**
 * B4: the play page's URL (PSP B3: the isolated PSP page for PPSSPP); params.js validates it on the other side.
 * Sega CD B6: `disc` (an external release) adds `&disc=1`, so a core shared by cartridges and discs loads from play-disc.
 * Multi-disc M5: `setName` (a PlayStation disc set's playlist) becomes the `name` and adds `&set=1`; `rom` stays Disc 1.
 */
export function playUrl(
	gameId: string,
	rom: Pick<RomDetail, 'id' | 'fileName'>,
	core: string,
	disc: boolean,
	save: PlaySave | null = null,
	setName: string | null = null
): string {
	const page = opensInNewTab(core) ? isolatedPages[core] : 'play.html';
	const name = setName ?? rom.fileName;
	let url = `/emulator/${page}?game=${gameId}&rom=${rom.id}&core=${core}&name=${encodeURIComponent(name)}`;
	if (disc) url += '&disc=1';
	if (setName !== null) url += '&set=1';
	// Saves B17: the library save the play page loads at game start.
	return save ? `${url}&save=${save.saveId}&saveKind=${save.saveKind}` : url;
}

/** Saves B17: a library save to load when the game starts. */
export interface PlaySave {
	saveId: string;
	saveKind: 'state' | 'battery';
}

/**
 * UI polish B22: records a browser play for the Activity report. Fire-and-forget: errors are ignored, and an expired
 * session's 401 doesn't send the player to the login page mid-game.
 */
export function recordPlay(gameId: string, romId: number): void {
	api
		.post(`/api/games/${encodeURIComponent(gameId)}/roms/${romId}/played`, undefined, {
			noRedirect: true
		})
		.catch(() => {});
}

/**
 * B22: a PSP `target="_blank"` play link records on click and on a middle-button click; "Open in new tab" from the
 * context menu fires neither, so it isn't counted.
 */
export function playLinkEvents(gameId: string, romId: number) {
	return {
		onclick: () => recordPlay(gameId, romId),
		onauxclick: (event: MouseEvent) => {
			if (event.button === 1) recordPlay(gameId, romId);
		}
	};
}

/** Release editing B25: a pixel is border when none of its channels is above this. */
const borderMax = 16;

/**
 * Release editing B25: the smallest rectangle outside which every row and column is border (`max(r, g, b) <= 16`). Rows
 * are trimmed from the top and bottom, then columns of the rows left. An all-border image keeps its full size.
 */
export function trimBounds(
	data: Uint8ClampedArray,
	width: number,
	height: number
): { x: number; y: number; width: number; height: number } {
	const border = (x: number, y: number) => {
		const i = (y * width + x) * 4;
		return Math.max(data[i], data[i + 1], data[i + 2]) <= borderMax;
	};
	const rowIsBorder = (y: number) => {
		for (let x = 0; x < width; x++) if (!border(x, y)) return false;
		return true;
	};
	let top = 0;
	while (top < height && rowIsBorder(top)) top++;
	if (top === height) return { x: 0, y: 0, width, height };
	let bottom = height - 1;
	while (rowIsBorder(bottom)) bottom--;
	const columnIsBorder = (x: number) => {
		for (let y = top; y <= bottom; y++) if (!border(x, y)) return false;
		return true;
	};
	let left = 0;
	while (columnIsBorder(left)) left++;
	let right = width - 1;
	while (columnIsBorder(right)) right--;
	return { x: left, y: top, width: right - left + 1, height: bottom - top + 1 };
}

/**
 * Release editing B25, B27: the PNG with its black border cut off, or null when nothing was trimmed or the image couldn't
 * be decoded or encoded.
 */
export async function trimScreenshot(png: ArrayBuffer): Promise<ArrayBuffer | null> {
	try {
		const bitmap = await createImageBitmap(new Blob([png], { type: 'image/png' }));
		const canvas = document.createElement('canvas');
		canvas.width = bitmap.width;
		canvas.height = bitmap.height;
		const context = canvas.getContext('2d')!;
		context.drawImage(bitmap, 0, 0);
		const bounds = trimBounds(
			context.getImageData(0, 0, bitmap.width, bitmap.height).data,
			bitmap.width,
			bitmap.height
		);
		if (bounds.width === bitmap.width && bounds.height === bitmap.height) return null;
		const out = document.createElement('canvas');
		out.width = bounds.width;
		out.height = bounds.height;
		out
			.getContext('2d')!
			.drawImage(
				bitmap,
				bounds.x,
				bounds.y,
				bounds.width,
				bounds.height,
				0,
				0,
				bounds.width,
				bounds.height
			);
		const blob = await new Promise<Blob | null>((done) => out.toBlob(done, 'image/png'));
		return blob ? await blob.arrayBuffer() : null;
	} catch {
		return null;
	}
}
