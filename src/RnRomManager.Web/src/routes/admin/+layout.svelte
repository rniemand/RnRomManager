<script lang="ts">
	import NavTabs from '#lib/components/layout/NavTabs.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';

	let { children }: { children: Snippet } = $props();

	const tabs = [
		// Release editing B33: an icon per tab.
		{ href: resolve('/admin/users'), label: 'Users', icon: 'admin-users' },
		{ href: resolve('/admin/dats'), label: 'DATs', icon: 'admin-dats' },
		{ href: resolve('/admin/settings'), label: 'Settings', icon: 'admin-settings' },
		{ href: resolve('/admin/rom-storage'), label: 'ROM Zip Config', icon: 'storage-in-zip' },
		{ href: resolve('/admin/bios'), label: 'BIOS info', icon: 'admin-bios' },
		{ href: resolve('/admin/actions'), label: 'Actions', icon: 'admin-actions' },
		{ href: resolve('/admin/backups'), label: 'Backups', icon: 'admin-backups' },
		{ href: resolve('/admin/logs'), label: 'Logs', icon: 'admin-logs' },
		{ href: resolve('/admin/audit'), label: 'Auditing', icon: 'admin-audit' }
	];
</script>

<AdminOnly>
	<PageTitle icon="admin" text="Admin" />
	<NavTabs
		tabs={tabs.map((tab) => ({ ...tab, id: tab.href }))}
		active={page.url.pathname}
		class="mb-3"
		linkClass="d-flex align-items-center gap-1"
	/>
	{@render children()}
</AdminOnly>
