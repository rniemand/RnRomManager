import { describe, expect, it } from 'vitest';
import {
	actionFor,
	actions,
	defaultBindings,
	errors,
	fromEvent,
	normalize,
	ruleError,
	validate,
	type Bindings
} from './hotkeys';

// Parity: the same cases as tests/RnRomManager.Core.Tests/Users/HotkeysTests.cs.

const key = (
	code: string,
	mods: Partial<Record<'ctrl' | 'alt' | 'shift' | 'meta', boolean>> = {}
) => ({
	code,
	ctrlKey: !!mods.ctrl,
	altKey: !!mods.alt,
	shiftKey: !!mods.shift,
	metaKey: !!mods.meta
});

const bindings = (overrides: Partial<Bindings> = {}): Bindings => ({
	...defaultBindings(),
	...overrides
});

describe('hotkey actions (B21)', () => {
	it('is the B21 table', () => {
		expect(actions.map((a) => [a.id, a.defaults.join(', '), a.editable, a.role])).toEqual([
			['search', 'Ctrl+K, Ctrl+Space, Meta+K', false, null],
			['nav.library', 'Alt+Shift+L', true, null],
			['nav.collections', 'Alt+Shift+C', true, null],
			['nav.ingestion', 'Alt+Shift+I', true, 'admin'],
			['nav.review', 'Alt+Shift+F', true, 'admin'],
			['review.next', 'Alt+Shift+G', true, 'admin']
		]);
	});
});

describe('normalize', () => {
	it.each([
		['Alt+Shift+L', 'Alt+Shift+L'],
		['shift+alt+l', 'Alt+Shift+L'],
		['Meta+Shift+Ctrl+Alt+9', 'Ctrl+Alt+Shift+Meta+9'],
		['Ctrl+F12', 'Ctrl+F12'],
		['ctrl+f1', 'Ctrl+F1'],
		[' Alt + X ', 'Alt+X']
	])('%s → %s', (input, expected) => expect(normalize(input)).toBe(expected));

	it.each([
		'',
		'Alt+Shift',
		'Alt+Alt+L',
		'Alt+L+M',
		'Alt+F13',
		'Alt+F0',
		'Alt+F01',
		'Alt+Space',
		'Alt+é',
		'Hyper+L',
		'Alt+'
	])('rejects %j', (input) => expect(normalize(input)).toBeNull());
});

describe('ruleError (B22)', () => {
	it.each([
		['Shift+L', errors.modifier],
		['L', errors.modifier],
		['Ctrl+Alt+Q', errors.altGr],
		['Ctrl+K', errors.reserved],
		['Meta+K', errors.reserved]
	])('rejects %s', (binding, error) => expect(ruleError(binding)).toBe(error));

	it.each(['Ctrl+Alt+Shift+Q', 'Ctrl+Alt+Meta+Q', 'Alt+Q', 'Meta+Shift+Q', 'Ctrl+Shift+K'])(
		'accepts %s',
		(binding) => expect(ruleError(binding)).toBeNull()
	);
});

describe('validate', () => {
	it('defaults are valid for every role', () => {
		expect(validate(defaultBindings(), ['admin'])).toEqual({});
		expect(validate(defaultBindings(), [])).toEqual({});
	});

	it('a duplicate among allowed actions errors on the later one', () => {
		expect(validate(bindings({ 'nav.review': 'Alt+Shift+L' }), ['admin'])).toEqual({
			'nav.review': 'Already used by Go to Library.'
		});
	});

	it("a User may take an Admin action's key", () => {
		expect(validate(bindings({ 'nav.library': 'Alt+Shift+I' }), [])).toEqual({});
		expect(validate(bindings({ 'nav.library': 'Alt+Shift+I' }), ['admin'])).toEqual({
			'nav.ingestion': 'Already used by Go to Library.'
		});
	});

	it('reports format and rule errors; null is allowed', () => {
		expect(
			validate(
				bindings({
					'nav.library': 'Ctrl+K',
					'nav.ingestion': 'Ctrl+Alt+I',
					'nav.review': 'Shift+',
					'review.next': null
				}),
				['admin']
			)
		).toEqual({
			'nav.library': errors.reserved,
			'nav.ingestion': errors.altGr,
			'nav.review': errors.format
		});
	});
});

describe('fromEvent', () => {
	it('reads the key from code, modifiers in canonical order', () => {
		expect(fromEvent(key('KeyI', { alt: true, shift: true }))).toBe('Alt+Shift+I');
		expect(fromEvent(key('Digit5', { meta: true, ctrl: true }))).toBe('Ctrl+Meta+5');
		expect(fromEvent(key('F12', { ctrl: true }))).toBe('Ctrl+F12');
	});

	it("uses the physical key, so Mac Option letters (event.key '¬') still read as the letter", () => {
		expect(fromEvent({ ...key('KeyL', { alt: true }), key: '¬' } as never)).toBe('Alt+L');
	});

	it('ignores modifiers alone and keys outside A–Z, 0–9, F1–F12', () => {
		expect(fromEvent(key('ShiftLeft', { shift: true }))).toBeNull();
		expect(fromEvent(key('Space', { ctrl: true }))).toBeNull();
		expect(fromEvent(key('Numpad5', { alt: true }))).toBeNull();
		expect(fromEvent(key('F13', { alt: true }))).toBeNull();
	});
});

describe('actionFor', () => {
	it('matches only actions the roles allow', () => {
		expect(actionFor('Alt+Shift+L', defaultBindings(), [])).toBe('nav.library');
		expect(actionFor('Alt+Shift+I', defaultBindings(), [])).toBeNull();
		expect(actionFor('Alt+Shift+I', defaultBindings(), ['admin'])).toBe('nav.ingestion');
		expect(actionFor('Alt+Shift+Q', defaultBindings(), ['admin'])).toBeNull();
	});

	it('a duplicated binding fires only the first action in table order', () => {
		const dup = bindings({ 'review.next': 'Alt+Shift+F' });
		expect(actionFor('Alt+Shift+F', dup, ['admin'])).toBe('nav.review');
	});

	it('an unassigned action never matches', () => {
		expect(actionFor('Alt+Shift+L', bindings({ 'nav.library': null }), [])).toBeNull();
	});
});
