/** Review "manual entry" form (spec §7.5, §23 resolve `Manual`), checked client-side; the API checks again. */
import { titleError } from './gameEdit';

export interface ManualForm {
	title: string;
	/** Comma-separated (No-Intro region and language names contain no commas). */
	regions: string;
	languages: string;
	revision: string;
	flags: string;
	/** B17: an optional disc number (disc systems only); null when empty. */
	disc: number | null;
}

export type ManualErrors = Partial<Record<keyof ManualForm, string>>;

export interface ManualRequest {
	action: 'Manual';
	title: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	flags: string[];
	disc?: number;
}

export const emptyManualForm = (): ManualForm => ({
	title: '',
	regions: '',
	languages: '',
	revision: '',
	flags: '',
	disc: null
});

/** B9: a disc number is a whole number 1–99; `required` for Attach as a disc. */
export function discError(disc: number | null | undefined, required = false): string | undefined {
	if (disc === null || disc === undefined || Number.isNaN(disc))
		return required ? 'A disc number is required.' : undefined;
	return Number.isInteger(disc) && disc >= 1 && disc <= 99 ? undefined : 'Must be 1–99.';
}

/** B10: the flag a disc number becomes, e.g. `Disc 2`; a typed `Disc n` flag is replaced by it. */
const discFlag = /^disc \d+$/i;

/** `USA, Europe,` → `['USA', 'Europe']`. */
export function splitList(text: string): string[] {
	return text
		.split(',')
		.map((item) => item.trim())
		.filter(Boolean);
}

export function validateManual(form: ManualForm): ManualErrors {
	const errors: ManualErrors = {};
	const title = titleError(form.title);
	if (title) errors.title = title;
	if (form.revision.trim().length > 32) errors.revision = 'At most 32 characters.';
	const disc = discError(form.disc);
	if (disc) errors.disc = disc;
	return errors;
}

export function toManualRequest(form: ManualForm): ManualRequest {
	return {
		action: 'Manual',
		title: form.title.trim(),
		regions: splitList(form.regions),
		languages: splitList(form.languages),
		revision: form.revision.trim() || null,
		flags: splitList(form.flags),
		...(form.disc === null ? {} : { disc: form.disc })
	};
}

/** The §7.5 file name the manual entry produces (shown as a preview), without the extension. */
export function manualFileStem(form: ManualForm): string {
	const flags = splitList(form.flags);
	const groups = [
		splitList(form.regions).join(', '),
		form.revision.trim(),
		...(form.disc === null
			? flags
			: [...flags.filter((f) => !discFlag.test(f)), `Disc ${form.disc}`])
	];
	return [form.title.trim(), ...groups.filter(Boolean).map((g) => `(${g})`)].join(' ');
}
