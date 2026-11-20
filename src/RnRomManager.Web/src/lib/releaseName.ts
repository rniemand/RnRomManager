/** Release editing B7: the tags a release edit can change that show in its file name. */
export interface ReleaseTags {
	regions: string[];
	languages: string[];
	revision: string | null;
	hack: boolean;
	translated: boolean;
	version: string | null;
}

type Insert = 'default' | 'afterTitle' | 'end';

const list = (values: string[], separator: string) =>
	values.length === 0 ? null : `(${values.join(separator)})`;
const paren = (value: string | null) => (value ? `(${value})` : null);
const same = (a: string[], b: string[]) => a.length === b.length && a.every((v, i) => v === b[i]);

function change(
	stem: string,
	baseTitle: string,
	oldGroup: string | null,
	newGroup: string | null,
	insert: Insert
): string {
	const at = oldGroup === null ? -1 : stem.toLowerCase().indexOf(oldGroup.toLowerCase());
	if (at >= 0) {
		const end = at + oldGroup!.length;
		if (newGroup !== null) return stem.slice(0, at) + newGroup + stem.slice(end);
		const start = at > 0 && stem[at - 1] === ' ' ? at - 1 : at;
		return stem.slice(0, start) + stem.slice(end);
	}
	if (newGroup === null) return stem;
	if (insert === 'end') return stem + newGroup;
	if (
		insert === 'afterTitle' &&
		baseTitle.length > 0 &&
		stem.toLowerCase().startsWith(baseTitle.toLowerCase())
	)
		return `${stem.slice(0, baseTitle.length)} ${newGroup}${stem.slice(baseTitle.length)}`;
	const bracket = stem.indexOf('[');
	if (bracket < 0) return `${stem} ${newGroup}`;
	const before = bracket > 0 && stem[bracket - 1] === ' ' ? bracket - 1 : bracket;
	return `${stem.slice(0, before)} ${newGroup}${stem.slice(before)}`;
}

/**
 * B3 preview: a port of the server's `ReleaseFileName.Edit` (B7) that edits only the groups of the changed fields.
 * It skips sanitizing, the 180-character cut, the external name budget and the ` (2)` clash suffix: the server's
 * name wins and is shown in the success toast.
 */
export function editReleaseFileName(
	fileName: string,
	baseTitle: string,
	before: ReleaseTags,
	after: ReleaseTags
): string {
	const dot = fileName.lastIndexOf('.');
	const extension = dot > 0 ? fileName.slice(dot) : '';
	let stem = dot > 0 ? fileName.slice(0, dot) : fileName;
	const apply = (differs: boolean, from: string | null, to: string | null, insert: Insert) => {
		if (differs) stem = change(stem, baseTitle, from, to, insert);
	};
	apply(
		!same(before.regions, after.regions),
		list(before.regions, ', '),
		list(after.regions, ', '),
		'afterTitle'
	);
	apply(
		!same(before.languages, after.languages),
		list(before.languages, ','),
		list(after.languages, ','),
		'default'
	);
	apply(
		(before.revision ?? '') !== (after.revision ?? ''),
		paren(before.revision),
		paren(after.revision),
		'default'
	);
	apply(
		before.translated !== after.translated,
		before.translated ? '(Translated)' : null,
		after.translated ? '(Translated)' : null,
		'default'
	);
	apply(
		before.hack !== after.hack,
		before.hack ? '(Hack)' : null,
		after.hack ? '(Hack)' : null,
		'default'
	);
	apply(
		(before.version ?? '') !== (after.version ?? ''),
		before.version ? `[${before.version}]` : null,
		after.version ? `[${after.version}]` : null,
		'end'
	);
	return stem + extension;
}
