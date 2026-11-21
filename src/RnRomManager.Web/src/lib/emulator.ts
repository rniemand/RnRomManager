import { api } from './api/client';
import type { GameDetail, RomDetail } from './api/types';

/**
 * Play B1: the cores that play a release stored outside the ZIP through play-disc: a PlayStation disc, (PSP B2) a
 * PlayStation Portable UMD image, (Saturn B6) a Saturn disc and (Sega CD B5) a Sega CD disc on Genesis Plus GX, which
 * also plays Mega Drive cartridges from the ZIP: the play URL's `disc` flag tells the two apart. 3DO B7: Opera plays
 * only discs.
 */
export const externalBrowserCores: readonly string[] = [
	'pcsx_rearmed',
	'ppsspp',
	'yabause',
	'genesis_plus_gx',
	'opera',
	'desmume'
];

/** S8: the cores whose external releases load through play-disc; DeSmuME loads an external DS ROM from `/download`. */
export const playDiscCores: readonly string[] = [
	'pcsx_rearmed',
	'ppsspp',
	'yabause',
	'genesis_plus_gx',
	'opera'
];

/** S8: whether the play URL carries `disc=1`: an external release of a play-disc core. */
export function usesPlayDisc(core: string, rom: Pick<RomDetail, 'externalFileName'>): boolean {
	return rom.externalFileName !== null && playDiscCores.includes(core);
}

/** PSP B3: the core that plays only in the dedicated, cross-origin isolated tab. */
export const isolatedBrowserCore = 'ppsspp';

/** PSP B3: whether Play opens the core in its own tab instead of the modal. */
export function opensInNewTab(core: string | null): boolean {
	return core === isolatedBrowserCore;
}

/**
 * Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. Play B1: a PlayStation
 * or PSP release is external and plays anyway, and (Saturn B6) a Saturn bin/cue disc; other external releases (Switch,
 * GameCube…) don't, nor does an .nrg image. CHD B5: a standalone .chd plays like an .iso (PlayStation, Saturn, Sega CD).
 */
export function canPlay(
	game: Pick<GameDetail, 'browserCore'>,
	rom: Pick<RomDetail, 'externalFileName'>
): boolean {
	return (
		game.browserCore !== null &&
		(rom.externalFileName === null || externalBrowserCores.includes(game.browserCore)) &&
		// Saturn B6: an .nrg image never plays, whatever the core.
		!(rom.externalFileName?.toLowerCase().endsWith('.nrg') ?? false)
	);
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
	const page = opensInNewTab(core) ? 'play-psp.html' : 'play.html';
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
