<script lang="ts">
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import Icon from '#lib/components/Icon.svelte';

	let { children }: { children: Snippet } = $props();

	const tabs = [
		{ href: resolve('/admin/users'), label: 'Users' },
		{ href: resolve('/admin/dats'), label: 'DATs' },
		{ href: resolve('/admin/settings'), label: 'Settings' },
		{ href: resolve('/admin/about'), label: 'About' }
	];
</script>

<AdminOnly>
	<h1 class="h3 mb-3"><Icon name="admin" /> Admin</h1>
	<ul class="nav nav-tabs mb-3">
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
