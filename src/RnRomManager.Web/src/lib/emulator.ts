import type { GameDetail, RomDetail } from './api/types';

/** Browser emulators B3: the system has an EmulatorJS core and the release is inside the ZIP. */
export function canPlay(
	game: Pick<GameDetail, 'browserCore'>,
	rom: Pick<RomDetail, 'externalFileName'>
): boolean {
	return game.browserCore !== null && rom.externalFileName === null;
}

/** B4: the play page's URL; params.js validates it on the other side. */
export function playUrl(
	gameId: string,
	rom: Pick<RomDetail, 'id' | 'fileName'>,
	core: string
): string {
	return `/emulator/play.html?game=${gameId}&rom=${rom.id}&core=${core}&name=${encodeURIComponent(rom.fileName)}`;
}
