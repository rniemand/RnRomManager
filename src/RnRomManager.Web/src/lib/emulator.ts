import type { GameDetail, RomDetail } from './api/types';

/**
 * Play B1: the cores that play a release stored outside the ZIP through play-disc: a PlayStation disc and (PSP B2) a
 * PlayStation Portable UMD image.
 */
export const externalBrowserCores: readonly string[] = ['pcsx_rearmed', 'ppsspp'];

/** PSP B3: the core that plays only in the dedicated, cross-origin isolated tab. */
export const isolatedBrowserCore = 'ppsspp';

/** PSP B3: whether Play opens the core in its own tab instead of the modal. */
export function opensInNewTab(core: string | null): boolean {
	return core === isolatedBrowserCore;
}

/**
 * Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. Play B1: a PlayStation
 * or PSP release is external and plays anyway; other external releases (Switch, Saturn…) don't.
 */
export function canPlay(
	game: Pick<GameDetail, 'browserCore'>,
	rom: Pick<RomDetail, 'externalFileName'>
): boolean {
	return (
		game.browserCore !== null &&
		(rom.externalFileName === null || externalBrowserCores.includes(game.browserCore))
	);
}

/** B4: the play page's URL (PSP B3: the isolated PSP page for PPSSPP); params.js validates it on the other side. */
export function playUrl(
	gameId: string,
	rom: Pick<RomDetail, 'id' | 'fileName'>,
	core: string
): string {
	const page = opensInNewTab(core) ? 'play-psp.html' : 'play.html';
	return `/emulator/${page}?game=${gameId}&rom=${rom.id}&core=${core}&name=${encodeURIComponent(rom.fileName)}`;
}
