<script lang="ts">
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import Icon from '#lib/components/Icon.svelte';

	let { children }: { children: Snippet } = $props();

	// RP1: every signed-in user; the actions inside keep their own roles.
	const tabs = [
		{ href: resolve('/reports/library-size'), label: 'Library Size' },
		{ href: resolve('/reports/missing-images'), label: 'Missing Images' },
		{ href: resolve('/reports/duplicates'), label: 'Duplicates' }
	];
</script>

<h1 class="h3 mb-3"><Icon name="reports" /> Reports</h1>
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
