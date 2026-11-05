/** Edit-metadata form (spec §11 Admin, §23 `PATCH /api/games/{id}` rules), checked client-side; the API checks again. */
import type { GameDetail } from './api/types';

export interface GameEditForm {
	title: string;
	releaseYear: string;
	/** One item per line. */
	developers: string;
	publishers: string;
	genres: string;
	description: string;
}

export type GameEditErrors = Partial<Record<keyof GameEditForm, string>>;

export interface GamePatch {
	title: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
}

const listRule = 'At most 20 items of 1–255 characters each.';

export function toForm(game: GameDetail): GameEditForm {
	return {
		title: game.title,
		releaseYear: game.releaseYear?.toString() ?? '',
		developers: game.developers.join('\n'),
		publishers: game.publishers.join('\n'),
		genres: game.genres.join('\n'),
		description: game.description ?? ''
	};
}

/** Lines trimmed, blanks dropped, duplicates removed (the API's "trimmed, distinct"). */
export function toList(text: string): string[] {
	return [...new Set(text.split('\n').map((line) => line.trim()))].filter(Boolean);
}

/** §6.1: 1–255 chars after trim, and the file name must not sanitize to empty (trailing dots/spaces are trimmed). */
export function titleError(value: string): string | undefined {
	const title = value.trim();
	if (title.length < 1 || title.length > 255) return 'Use 1–255 characters.';
	if (!title.replace(/[\s.]+$/, '')) return 'Must contain more than dots and spaces.';
	return undefined;
}

export function validateGameEdit(form: GameEditForm): GameEditErrors {
	const errors: GameEditErrors = {};
	const title = titleError(form.title);
	if (title) errors.title = title;
	const year = form.releaseYear.trim();
	if (year && !(/^\d{4}$/.test(year) && Number(year) >= 1970 && Number(year) <= 2100))
		errors.releaseYear = 'Leave empty or enter a year 1970–2100.';
	for (const field of ['developers', 'publishers', 'genres'] as const) {
		const items = toList(form[field]);
		if (items.length > 20 || items.some((item) => item.length > 255)) errors[field] = listRule;
	}
	if (form.description.length > 4000) errors.description = 'At most 4000 characters.';
	return errors;
}

export function toPatch(form: GameEditForm): GamePatch {
	const year = form.releaseYear.trim();
	return {
		title: form.title.trim(),
		releaseYear: year ? Number(year) : null,
		developers: toList(form.developers),
		publishers: toList(form.publishers),
		genres: toList(form.genres),
		description: form.description.trim() ? form.description : null
	};
}
