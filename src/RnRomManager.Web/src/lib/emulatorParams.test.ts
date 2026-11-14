import { describe, expect, it } from 'vitest';
import {
	biosError,
	biosFor,
	controlSchemeFor,
	parsePlayParams
} from '../../static/emulator/params.js';

const game = '0f8fad5b-d9cb-469f-a165-70867728950e';

function query(overrides: Record<string, string | null>): string {
	const values: Record<string, string | null> = {
		game,
		rom: '12',
		core: 'gambatte',
		name: 'Orbit Blocks (World).gb',
		...overrides
	};
	const params = new URLSearchParams();
	for (const [key, value] of Object.entries(values)) if (value !== null) params.set(key, value);
	return `?${params}`;
}

describe('parsePlayParams', () => {
	it('parses a valid query', () => {
		expect(parsePlayParams(query({ core: 'mgba', name: 'A #1 & B.gba' }))).toEqual({
			game,
			rom: 12,
			core: 'mgba',
			name: 'A #1 & B.gba'
		});
	});

	it.each([
		'handy',
		'virtualjaguar',
		'beetle_vb',
		'a5200',
		'prosystem',
		'stella2014',
		'gearcoleco',
		'smsplus'
	])('accepts the %s core', (core) => {
		expect(parsePlayParams(query({ core }))?.core).toBe(core);
	});

	it.each([
		['a bad GUID', { game: 'not-a-guid' }],
		['rom 0', { rom: '0' }],
		['rom -1', { rom: '-1' }],
		['rom 1.5', { rom: '1.5' }],
		['an unknown core', { core: 'snes9x' }],
		['an empty name', { name: '' }],
		['a 256-char name', { name: 'a'.repeat(256) }],
		['a missing game', { game: null }],
		['a missing rom', { rom: null }],
		['a missing core', { core: null }],
		['a missing name', { name: null }]
	])('rejects %s', (_, overrides) => {
		expect(parsePlayParams(query(overrides))).toBeNull();
	});

	it('accepts a 255-char name', () => {
		expect(parsePlayParams(query({ name: 'a'.repeat(255) }))?.name).toHaveLength(255);
	});
});

describe('biosFor', () => {
	it('names the ColecoVision BIOS for gearcoleco', () => {
		expect(biosFor('gearcoleco')).toBe('colecovision.rom');
	});

	it.each(['gambatte', 'stella2014', 'smsplus', 'prosystem'])('needs none for %s', (core) => {
		expect(biosFor(core)).toBeNull();
	});
});

describe('biosError', () => {
	it.each([200, 204])('is null for %i', (status) => {
		expect(biosError('colecovision.rom', status)).toBeNull();
	});

	it('asks to sign in again on 401', () => {
		expect(biosError('colecovision.rom', 401)).toBe('Your session has expired. Sign in again.');
	});

	it('names the missing file and folder on 404', () => {
		expect(biosError('colecovision.rom', 404)).toBe(
			'This system needs its BIOS: ask an Admin to put colecovision.rom in the BIOS folder (Paths:Bios).'
		);
	});

	it('shows the status otherwise', () => {
		expect(biosError('colecovision.rom', 500)).toBe("Couldn't load the BIOS (HTTP 500).");
	});
});

describe('controlSchemeFor', () => {
	it.each(['Pocket Drift (World).gg', 'POCKET.GG'])('uses segaGG for smsplus %s', (name) => {
		expect(controlSchemeFor('smsplus', name)).toBe('segaGG');
	});

	it('keeps the default for an smsplus .sms release', () => {
		expect(controlSchemeFor('smsplus', 'Harbor Patrol (World).sms')).toBeNull();
	});

	it('keeps the default for another core with a .gg name', () => {
		expect(controlSchemeFor('gambatte', 'Odd.gg')).toBeNull();
	});
});
