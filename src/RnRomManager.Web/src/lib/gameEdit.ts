/** Edit-metadata form (spec §11 Admin, §23 `PATCH /api/games/{id}` rules), checked client-side; the API checks again. */
import type { GameDetail, IgdbMatch } from '#lib/types/index.js';

export interface GameEditForm {
	title: string;
	releaseYear: string;
	/** One item per line. */
	developers: string;
	publishers: string;
	genres: string;
	description: string;
	/** null = not linked, 0 = Not on IGDB, else the IGDB game id. */
	igdbId: number | null;
}

export type GameEditErrors = Partial<Record<keyof GameEditForm, string>>;

export interface GamePatch {
	title: string;
	releaseYear: number | null;
	developers: string[];
	publishers: string[];
	genres: string[];
	description: string | null;
	igdbId: number | null;
}

const listRule = 'At most 20 items of 1–255 characters each.';

export function toForm(game: GameDetail): GameEditForm {
	return {
		title: game.title,
		releaseYear: game.releaseYear?.toString() ?? '',
		developers: game.developers.join('\n'),
		publishers: game.publishers.join('\n'),
		genres: game.genres.join('\n'),
		description: game.description ?? '',
		igdbId: game.igdbId
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
		description: form.description.trim() ? form.description : null,
		igdbId: form.igdbId
	};
}

/** M3: the fields Lookup Info can copy from an IGDB match, in the compare table's order. */
export const copyFields = [
	'title',
	'releaseYear',
	'developers',
	'publishers',
	'genres',
	'description'
] as const;

export type CopyField = (typeof copyFields)[number];

export type CopyState = Record<CopyField, { disabled: boolean; checked: boolean }>;

const listFields = ['developers', 'publishers', 'genres'] as const;

function isListField(field: CopyField): field is (typeof listFields)[number] {
	return (listFields as readonly string[]).includes(field);
}

/** M3's comparison: each side as the form would hold it (trimmed text, or the form's list). */
function sides(form: GameEditForm, match: IgdbMatch, field: CopyField): [string[], string[]] {
	if (isListField(field)) return [toList(form[field]), match[field]];
	const current = form[field].trim();
	const igdb =
		field === 'title'
			? match.name.trim()
			: field === 'releaseYear'
				? (match.releaseYear?.toString() ?? '')
				: (match.description ?? '').trim();
	return [current ? [current] : [], igdb ? [igdb] : []];
}

/** M3: a row is disabled when the IGDB value is empty or equals the form's; M2 (Timeline polish): every other row starts ticked, Title included. */
export function defaultCopyFields(form: GameEditForm, match: IgdbMatch): CopyState {
	return Object.fromEntries(
		copyFields.map((field) => {
			const [current, igdb] = sides(form, match, field);
			const equal = current.length === igdb.length && current.every((v, i) => v === igdb[i]);
			const disabled = !igdb.length || equal;
			return [field, { disabled, checked: !disabled }];
		})
	) as CopyState;
}

/** M3 Use this match: copies the given fields (lists one per line) and links the match. */
export function applyMatch(
	form: GameEditForm,
	match: IgdbMatch,
	fields: readonly CopyField[]
): GameEditForm {
	const next = { ...form, igdbId: match.igdbId };
	for (const field of fields) {
		if (isListField(field)) next[field] = match[field].join('\n');
		else if (field === 'title') next.title = match.name;
		else if (field === 'releaseYear') next.releaseYear = match.releaseYear?.toString() ?? '';
		else next.description = match.description ?? '';
	}
	return next;
}
