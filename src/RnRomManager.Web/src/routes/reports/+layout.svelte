<script lang="ts">
	import NavTabs from '#lib/components/layout/NavTabs.svelte';
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
	<NavTabs
		tabs={tabs.map((tab) => ({ ...tab, id: tab.href }))}
		active={page.url.pathname}
		class="mb-3"
	/>
	{@render children()}
{/if}
