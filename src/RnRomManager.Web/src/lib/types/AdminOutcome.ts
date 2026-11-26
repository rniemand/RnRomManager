import type { GameDetail } from './GameDetail.js';

/** What the detail page does next: show this game, go to another game (null = Library), or reload. */
export type AdminOutcome =
	| { game: GameDetail; message: string; warning?: boolean }
	| { goTo: string | null; message: string }
	| { reload: true; message?: string; warning?: boolean };
