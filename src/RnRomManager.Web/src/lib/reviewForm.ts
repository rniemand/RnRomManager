/** Review "manual entry" form (spec §7.5, §23 resolve `Manual`), checked client-side; the API checks again. */
import { titleError } from './gameEdit';

export interface ManualForm {
	title: string;
	/** Comma-separated (No-Intro region and language names contain no commas). */
	regions: string;
	languages: string;
	revision: string;
	flags: string;
}

export type ManualErrors = Partial<Record<keyof ManualForm, string>>;

export interface ManualRequest {
	action: 'Manual';
	title: string;
	regions: string[];
	languages: string[];
	revision: string | null;
	flags: string[];
}

export const emptyManualForm = (): ManualForm => ({
	title: '',
	regions: '',
	languages: '',
	revision: '',
	flags: ''
});

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
	return errors;
}

export function toManualRequest(form: ManualForm): ManualRequest {
	return {
		action: 'Manual',
		title: form.title.trim(),
		regions: splitList(form.regions),
		languages: splitList(form.languages),
		revision: form.revision.trim() || null,
		flags: splitList(form.flags)
	};
}

/** The §7.5 file name the manual entry produces (shown as a preview), without the extension. */
export function manualFileStem(form: ManualForm): string {
	const groups = [
		splitList(form.regions).join(', '),
		form.revision.trim(),
		...splitList(form.flags)
	];
	return [form.title.trim(), ...groups.filter(Boolean).map((g) => `(${g})`)].join(' ');
}
