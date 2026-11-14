import type { GameDetail, RomDetail } from './api/types';

/** Play B1: the one core that plays a release stored outside the ZIP (a PlayStation disc, through play-disc). */
export const externalBrowserCore = 'pcsx_rearmed';

/**
 * Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. Play B1: a PlayStation
 * release is external and plays anyway; other external releases (Switch, Saturn…) don't.
 */
export function canPlay(
	game: Pick<GameDetail, 'browserCore'>,
	rom: Pick<RomDetail, 'externalFileName'>
): boolean {
	return (
		game.browserCore !== null &&
		(rom.externalFileName === null || game.browserCore === externalBrowserCore)
	);
}

/** B4: the play page's URL; params.js validates it on the other side. */
export function playUrl(
	gameId: string,
	rom: Pick<RomDetail, 'id' | 'fileName'>,
	core: string
): string {
	return `/emulator/play.html?game=${gameId}&rom=${rom.id}&core=${core}&name=${encodeURIComponent(rom.fileName)}`;
}
