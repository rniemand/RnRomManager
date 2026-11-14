import type { GameDetail, RomDetail } from './api/types';

/**
 * Play B1: the cores that play a release stored outside the ZIP through play-disc: a PlayStation disc, (PSP B2) a
 * PlayStation Portable UMD image, (Saturn B6) a Saturn disc and (Sega CD B5) a Sega CD disc on Genesis Plus GX, which
 * also plays Mega Drive cartridges from the ZIP: the play URL's `disc` flag tells the two apart.
 */
export const externalBrowserCores: readonly string[] = [
	'pcsx_rearmed',
	'ppsspp',
	'yabause',
	'genesis_plus_gx'
];

/** PSP B3: the core that plays only in the dedicated, cross-origin isolated tab. */
export const isolatedBrowserCore = 'ppsspp';

/** PSP B3: whether Play opens the core in its own tab instead of the modal. */
export function opensInNewTab(core: string | null): boolean {
	return core === isolatedBrowserCore;
}

/**
 * Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. Play B1: a PlayStation
 * or PSP release is external and plays anyway, and (Saturn B6) a Saturn bin/cue disc; other external releases (Switch,
 * GameCube…) don't, nor does an .nrg image.
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
 */
export function playUrl(
	gameId: string,
	rom: Pick<RomDetail, 'id' | 'fileName'>,
	core: string,
	disc: boolean
): string {
	const page = opensInNewTab(core) ? 'play-psp.html' : 'play.html';
	const url = `/emulator/${page}?game=${gameId}&rom=${rom.id}&core=${core}&name=${encodeURIComponent(rom.fileName)}`;
	return disc ? `${url}&disc=1` : url;
}
