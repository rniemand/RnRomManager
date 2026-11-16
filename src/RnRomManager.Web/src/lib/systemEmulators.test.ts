import { describe, expect, it } from 'vitest';
import { emulatorGroups, linkHost, systemEmulators } from './systemEmulators';

describe('emulatorGroups (Systems tab B7)', () => {
	it('orders the groups Windows → iOS and keeps catalog order inside each', () => {
		const groups = emulatorGroups('gb');
		expect(groups.map((g) => g.label)).toEqual(['Windows', 'macOS', 'Linux', 'Android', 'iOS']);
		expect(groups[0].emulators.map((e) => e.name)).toEqual(['mGBA', 'SameBoy', 'RetroArch']);
		expect(groups[4].emulators.map((e) => e.name)).toEqual(['SameBoy', 'Delta', 'RetroArch']);
	});

	it('puts an emulator in every group its os list names', () => {
		const groups = emulatorGroups('gb');
		expect(
			groups.filter((g) => g.emulators.some((e) => e.name === 'SameBoy')).map((g) => g.os)
		).toEqual(['windows', 'macos', 'ios']);
	});

	it('drops empty groups', () => {
		expect(emulatorGroups('x360').map((g) => g.os)).toEqual(['windows', 'linux']);
	});

	it.each(['nope', '', 'GB', 'constructor', '__proto__'])(
		'returns [] for unknown system %j',
		(id) => {
			expect(emulatorGroups(id)).toEqual([]);
		}
	);

	it('returns [] for a system with an empty list', () => {
		systemEmulators.empty = [];
		try {
			expect(emulatorGroups('empty')).toEqual([]);
		} finally {
			delete systemEmulators.empty;
		}
	});
});

describe('linkHost', () => {
	it('is the hostname unchanged', () => {
		expect(linkHost('https://www.retroarch.com/?page=platforms')).toBe('www.retroarch.com');
		expect(linkHost('https://github.com/drhelius/Gearsystem')).toBe('github.com');
	});
});
