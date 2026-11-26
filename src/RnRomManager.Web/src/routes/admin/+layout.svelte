<script lang="ts">
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import Icon from '#lib/icons/Icon.svelte';

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
	<ul class="nav nav-tabs mb-3" use:scrollActiveTab>
		{#each tabs as tab (tab.href)}
			<li class="nav-item">
				<a
					class="nav-link d-flex align-items-center gap-1"
					class:active={page.url.pathname === tab.href}
					aria-current={page.url.pathname === tab.href ? 'page' : undefined}
					href={tab.href}><Icon name={tab.icon} /> {tab.label}</a
				>
			</li>
		{/each}
	</ul>
	{@render children()}
</AdminOnly>
