import type { Role } from './api/types';

/**
 * B21–B22 hotkey actions and binding rules. Mirrors `RnRomManager.Core/Users/Hotkeys.cs`; keep both in step (the
 * parity cases in `hotkeys.test.ts` match `HotkeysTests.cs`). A binding's canonical form is
 * `Ctrl+Alt+Shift+Meta+<Key>` (modifiers in that order), `<Key>` being `A`–`Z`, `0`–`9` or `F1`–`F12`.
 */

export type HotkeyId =
	'search' | 'nav.library' | 'nav.collections' | 'nav.ingestion' | 'nav.review' | 'review.next';
export type EditableHotkeyId = Exclude<HotkeyId, 'search'>;
export type Bindings = Record<EditableHotkeyId, string | null>;

export interface HotkeyAction {
	id: HotkeyId;
	name: string;
	defaults: string[];
	editable: boolean;
	/** null = every signed-in user. */
	role: Role | null;
}

/** B21's table, in its order (which also decides which of two duplicates fires). */
export const actions: readonly HotkeyAction[] = [
	{
		id: 'search',
		name: 'Open global search',
		defaults: ['Ctrl+K', 'Ctrl+Space', 'Meta+K'],
		editable: false,
		role: null
	},
	{
		id: 'nav.library',
		name: 'Go to Library',
		defaults: ['Alt+Shift+L'],
		editable: true,
		role: null
	},
	{
		id: 'nav.collections',
		name: 'Go to Collections',
		defaults: ['Alt+Shift+C'],
		editable: true,
		role: null
	},
	{
		id: 'nav.ingestion',
		name: 'Go to Ingestion',
		defaults: ['Alt+Shift+I'],
		editable: true,
		role: 'admin'
	},
	{
		id: 'nav.review',
		name: 'Go to Review',
		defaults: ['Alt+Shift+F'],
		editable: true,
		role: 'admin'
	},
	{
		id: 'review.next',
		name: 'Open the first review item',
		defaults: ['Alt+Shift+G'],
		editable: true,
		role: 'admin'
	}
];

export const editableActions = actions.filter((a) => a.editable) as (HotkeyAction & {
	id: EditableHotkeyId;
})[];

/** Global search's own bindings: no action may take them. */
export const reserved: readonly string[] = actions[0].defaults;

export const errors = {
	format: 'Use Ctrl, Alt, Shift or Meta plus one key: A–Z, 0–9 or F1–F12.',
	modifier: 'Include Ctrl, Alt or Meta.',
	altGr: 'Ctrl+Alt also needs Shift or Meta: on many keyboards it types characters.',
	reserved: 'Reserved for global search.',
	duplicate: (name: string) => `Already used by ${name}.`
} as const;

const modifierOrder = ['Ctrl', 'Alt', 'Shift', 'Meta'] as const;

export function defaultBindings(): Bindings {
	return Object.fromEntries(editableActions.map((a) => [a.id, a.defaults[0]])) as Bindings;
}

export function allowedFor(action: HotkeyAction, roles: readonly string[]): boolean {
	return action.role === null || roles.includes(action.role);
}

function normalizeKey(token: string): string | null {
	if (/^[A-Za-z0-9]$/.test(token)) return token.toUpperCase();
	const f = /^[Ff]([1-9]|1[0-2])$/.exec(token);
	return f ? `F${f[1]}` : null;
}

/** The canonical form (any modifier order and letter case), or null when it isn't a binding. */
export function normalize(combination: string | null | undefined): string | null {
	if (!combination || !combination.trim()) return null;
	const modifiers = new Set<string>();
	let key: string | null = null;
	for (const raw of combination.split('+')) {
		const token = raw.trim();
		const modifier = modifierOrder.find((m) => m.toLowerCase() === token.toLowerCase());
		if (modifier) {
			if (modifiers.has(modifier)) return null;
			modifiers.add(modifier);
		} else if (key === null && normalizeKey(token)) {
			key = normalizeKey(token);
		} else {
			return null;
		}
	}
	if (key === null) return null;
	return [...modifierOrder.filter((m) => modifiers.has(m)), key].join('+');
}

/** B22's rules for one canonical binding; duplicates are checked by {@link validate}. */
export function ruleError(canonical: string): string | null {
	const parts = canonical.split('+');
	const has = (m: string) => parts.includes(m);
	if (!has('Ctrl') && !has('Alt') && !has('Meta')) return errors.modifier;
	if (has('Ctrl') && has('Alt') && !has('Shift') && !has('Meta')) return errors.altGr;
	if (reserved.includes(canonical)) return errors.reserved;
	return null;
}

/** Format, rule and duplicate errors per action the roles allow; the later duplicate in table order gets the error. */
export function validate(
	bindings: Bindings,
	roles: readonly string[]
): Partial<Record<EditableHotkeyId, string>> {
	const result: Partial<Record<EditableHotkeyId, string>> = {};
	const taken = new Map<string, HotkeyAction>();
	for (const action of editableActions.filter((a) => allowedFor(a, roles))) {
		const value = bindings[action.id];
		if (value === null) continue;
		const canonical = normalize(value);
		const error = canonical === null ? errors.format : ruleError(canonical);
		if (error) {
			result[action.id] = error;
			continue;
		}
		const first = taken.get(canonical!);
		if (first) result[action.id] = errors.duplicate(first.name);
		else taken.set(canonical!, action);
	}
	return result;
}

/** The key part of a binding from `KeyboardEvent.code`, so Alt/Option and layouts don't change the letter. */
function keyFromCode(code: string): string | null {
	let m = /^Key([A-Z])$/.exec(code);
	if (m) return m[1];
	m = /^Digit([0-9])$/.exec(code);
	if (m) return m[1];
	m = /^F([1-9]|1[0-2])$/.exec(code);
	return m ? `F${m[1]}` : null;
}

type KeyLike = Pick<KeyboardEvent, 'code' | 'ctrlKey' | 'altKey' | 'shiftKey' | 'metaKey'>;

/** The canonical binding a keydown represents, or null for a modifier alone or an unsupported key. */
export function fromEvent(e: KeyLike): string | null {
	const key = keyFromCode(e.code);
	if (!key) return null;
	const pressed = { Ctrl: e.ctrlKey, Alt: e.altKey, Shift: e.shiftKey, Meta: e.metaKey };
	return [...modifierOrder.filter((m) => pressed[m]), key].join('+');
}

/** The first action (B21 order) the roles allow whose binding is `combination`. */
export function actionFor(
	combination: string,
	bindings: Bindings,
	roles: readonly string[]
): EditableHotkeyId | null {
	return (
		editableActions.find((a) => allowedFor(a, roles) && bindings[a.id] === combination)?.id ?? null
	);
}
