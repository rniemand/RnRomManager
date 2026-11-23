import { describe, expect, it, vi } from 'vitest';

vi.mock('$app/paths', () => ({
	resolve: (path: string, params?: { id: string }) => path.replace('[id]', params?.id ?? '')
}));

const { reportTabs } = await import('./reports');

describe('reportTabs (S1)', () => {
	const site = [
		['/reports/newly-added', 'Newly Added', 'report-new-games'],
		['/reports/library-size', 'Library Size', 'report-size'],
		['/reports/missing-images', 'Missing Images', 'report-missing-images'],
		['/reports/duplicates', 'Duplicates', 'report-duplicates'],
		['/reports/multiple-releases', 'Multiple Releases', 'report-multiple-releases'],
		['/reports/hacks', 'Games With Hacks', 'report-hacks'],
		['/reports/image-resolutions', 'Image Resolutions', 'report-image-resolutions']
	].map(([href, label, icon]) => ({ href, label, icon }));

	it('lists the Site reports in tab order', () => {
		expect(reportTabs(false)).toEqual(site);
	});

	it('adds Missing Metadata, Missing IGDB IDs and Activity, last, for Admins', () => {
		expect(reportTabs(true)).toEqual([
			...site,
			{
				href: '/reports/missing-metadata',
				label: 'Missing Metadata',
				icon: 'report-missing-metadata'
			},
			{
				href: '/reports/missing-igdb-ids',
				label: 'Missing IGDB IDs',
				icon: 'report-missing-igdb'
			},
			{ href: '/reports/activity', label: 'Activity', icon: 'activity' }
		]);
	});
});
