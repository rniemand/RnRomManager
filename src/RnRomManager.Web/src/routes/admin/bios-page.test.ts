import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { resetClient } from '#lib/api/client.js';
import type { BiosInfo } from '#lib/types/index.js';
import { formatSize } from '#lib/format.js';
import { auth } from '#lib/stores/auth.svelte.js';

const { default: BiosPage } = await import('./bios/+page.svelte');

const info: BiosInfo = {
	folder: '/app/bios',
	setting: 'Paths:Bios',
	env: 'Paths__Bios',
	folderExists: true,
	groups: [
		{
			system: 'ColecoVision',
			need: 'All',
			ok: true,
			note: 'Gearcoleco needs it for every game.',
			files: [
				{
					name: 'colecovision.rom',
					minBytes: 8192,
					maxBytes: 8192,
					status: 'Valid',
					size: 8192,
					md5: 'abc123'
				}
			],
			coreSetting: null,
			coreEnv: null,
			coreConfigured: null
		},
		{
			system: '3DO',
			need: 'OneOf',
			ok: false,
			note: 'Every one present must be valid.',
			files: [
				{
					name: 'panafz10.bin',
					minBytes: 1048576,
					maxBytes: 1048576,
					status: 'WrongSize',
					size: 10,
					md5: null
				}
			],
			coreSetting: 'Emulator:OperaCorePath',
			coreEnv: 'Emulator__OperaCorePath',
			coreConfigured: false
		},
		{
			system: 'Arcade (FinalBurn Neo)',
			need: 'Optional',
			ok: true,
			note: 'Board BIOS sets.',
			files: [],
			coreSetting: 'Emulator:FbneoCorePath',
			coreEnv: 'Emulator__FbneoCorePath',
			coreConfigured: true
		}
	],
	otherFiles: [{ name: 'neogeo.zip', size: 2048 }]
};

describe('BIOS info tab', () => {
	beforeEach(() => {
		resetClient();
		auth.set({ id: 1, username: 'admin', roles: ['admin'], mustChangePassword: false });
		vi.stubGlobal('fetch', (url: string) =>
			Promise.resolve(
				url === '/api/admin/bios'
					? new Response(JSON.stringify(info), { status: 200 })
					: new Response(null, { status: 404 })
			)
		);
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		auth.set(null);
	});

	it('lists each system with its files, status, size check and configuration', async () => {
		render(BiosPage);

		expect(await screen.findByText('colecovision.rom')).toBeTruthy();
		expect(screen.getByText('Valid')).toBeTruthy();
		expect(screen.getByText('abc123')).toBeTruthy();
		expect(screen.getByText('Wrong size')).toBeTruthy();
		expect(screen.getByText(formatSize(1048576))).toBeTruthy();
		expect(screen.getByRole('img', { name: 'Needs attention' })).toBeTruthy();
		expect(screen.getAllByRole('img', { name: 'Ready' })).toHaveLength(2);
		expect(screen.getByText('Emulator__OperaCorePath')).toBeTruthy();
		expect(screen.getByText('Not set')).toBeTruthy();
		expect(screen.getByText('Set')).toBeTruthy();
		expect(screen.getAllByText('Paths__Bios').length).toBeGreaterThan(1);
		expect(screen.getByText('neogeo.zip')).toBeTruthy();
	});
});
