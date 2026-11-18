<script lang="ts">
	import Icon from '#lib/components/Icon.svelte';
	import NotAllowed from '#lib/components/NotAllowed.svelte';
	import PageTitle from '#lib/components/PageTitle.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';

	let { children }: { children: Snippet } = $props();

	// RP1: every signed-in user; the actions inside keep their own roles. B20: Activity is Admin-only, its tab hidden.
	const activity = resolve('/reports/activity');
	const tabs = $derived([
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
		...(auth.isAdmin ? [{ href: activity, label: 'Activity', icon: 'activity' }] : [])
	]);
</script>

{#if page.url.pathname === activity && !auth.isAdmin}
	<!-- B20: one h1 for a Site user on the Admin-only route. -->
	<NotAllowed />
{:else}
	<PageTitle icon="reports" text="Reports" />
	<ul class="nav nav-tabs mb-3">
		{#each tabs as tab (tab.href)}
			<li class="nav-item">
				<a
					class="nav-link"
					class:active={page.url.pathname === tab.href}
					aria-current={page.url.pathname === tab.href ? 'page' : undefined}
					href={tab.href}><Icon name={tab.icon} /> {tab.label}</a
				>
			</li>
		{/each}
	</ul>
	{@render children()}
{/if}
