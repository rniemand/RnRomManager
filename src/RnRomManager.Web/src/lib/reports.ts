import { resolve } from '$app/paths';

/** A Reports tab: the layout's tab strip and Global Search's Reports group (S1) list the same ones. */
export type ReportTab = { href: string; label: string; icon: string };

/** The report tabs in order; Missing Metadata and Activity are Admin-only (B20, M4). Release editing B15: Newly Added first. */
export function reportTabs(isAdmin: boolean): ReportTab[] {
	return [
		{ href: resolve('/reports/newly-added'), label: 'Newly Added', icon: 'report-new-games' },
		{ href: resolve('/reports/library-size'), label: 'Library Size', icon: 'report-size' },
		{
			href: resolve('/reports/missing-images'),
			label: 'Missing Images',
			icon: 'report-missing-images'
		},
		{ href: resolve('/reports/duplicates'), label: 'Duplicates', icon: 'report-duplicates' },
		{
			href: resolve('/reports/multiple-releases'),
			label: 'Multiple Releases',
			icon: 'report-multiple-releases'
		},
		{ href: resolve('/reports/hacks'), label: 'Games With Hacks', icon: 'report-hacks' },
		{
			href: resolve('/reports/image-resolutions'),
			label: 'Image Resolutions',
			icon: 'report-image-resolutions'
		},
		...(isAdmin
			? [
					{
						href: resolve('/reports/missing-metadata'),
						label: 'Missing Metadata',
						icon: 'report-missing-metadata'
					},
					{ href: resolve('/reports/activity'), label: 'Activity', icon: 'activity' }
				]
			: [])
	];
}
