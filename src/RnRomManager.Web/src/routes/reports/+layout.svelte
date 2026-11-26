<script lang="ts">
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import Icon from '#lib/icons/Icon.svelte';
	import NotAllowed from '#lib/components/common/NotAllowed.svelte';
	import PageTitle from '#lib/components/layout/PageTitle.svelte';
	import { reportTabs } from '#lib/reports.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import type { Snippet } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';

	let { children }: { children: Snippet } = $props();

	// RP1: every signed-in user; the actions inside keep their own roles. B20: Activity is Admin-only, its tab hidden.
	// Missing Metadata M4 and Missing IGDB IDs R2: Admin-only too.
	const activity = resolve('/reports/activity');
	const missingMetadata = resolve('/reports/missing-metadata');
	const missingIgdbIds = resolve('/reports/missing-igdb-ids');
	const adminOnly: string[] = [activity, missingMetadata, missingIgdbIds];
	const tabs = $derived(reportTabs(auth.isAdmin));
</script>

{#if adminOnly.includes(page.url.pathname) && !auth.isAdmin}
	<!-- B20: one h1 for a Site user on the Admin-only route. -->
	<NotAllowed />
{:else}
	<PageTitle icon="reports" text="Reports" />
	<ul class="nav nav-tabs mb-3" use:scrollActiveTab>
		{#each tabs as tab (tab.href)}
			<li class="nav-item">
				<!-- eslint-disable svelte/no-navigation-without-resolve -- reportTabs' hrefs are resolved -->
				<a
					class="nav-link"
					class:active={page.url.pathname === tab.href}
					aria-current={page.url.pathname === tab.href ? 'page' : undefined}
					href={tab.href}><Icon name={tab.icon} /> {tab.label}</a
				>
				<!-- eslint-enable svelte/no-navigation-without-resolve -->
			</li>
		{/each}
	</ul>
	{@render children()}
{/if}
