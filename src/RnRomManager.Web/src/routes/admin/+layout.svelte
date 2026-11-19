<script lang="ts">
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import AdminOnly from '#lib/components/AdminOnly.svelte';

	let { children }: { children: Snippet } = $props();

	const tabs = [
		{ href: resolve('/admin/users'), label: 'Users' },
		{ href: resolve('/admin/dats'), label: 'DATs' },
		{ href: resolve('/admin/settings'), label: 'Settings' },
		{ href: resolve('/admin/rom-storage'), label: 'ROM Zip Config' },
		{ href: resolve('/admin/backups'), label: 'Backups' },
		{ href: resolve('/admin/logs'), label: 'Logs' },
		{ href: resolve('/admin/audit'), label: 'Auditing' }
	];
</script>

<AdminOnly>
	<PageTitle icon="admin" text="Admin" />
	<ul class="nav nav-tabs mb-3" use:scrollActiveTab>
		{#each tabs as tab (tab.href)}
			<li class="nav-item">
				<a
					class="nav-link"
					class:active={page.url.pathname === tab.href}
					aria-current={page.url.pathname === tab.href ? 'page' : undefined}
					href={tab.href}>{tab.label}</a
				>
			</li>
		{/each}
	</ul>
	{@render children()}
</AdminOnly>
