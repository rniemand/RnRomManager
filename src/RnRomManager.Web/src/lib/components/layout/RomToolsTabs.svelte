<script lang="ts" module>
	/** The ROM Tools pages, in tab order; the navbar's ROM Tools link opens the first. */
	export const romTools = [
		{ href: '/ingestion', label: 'Ingestion', icon: 'ingest' },
		{ href: '/review', label: 'Review', icon: 'review' },
		{ href: '/deleted', label: 'Deleted ROMs', icon: 'deleted' },
		{ href: '/integrity', label: 'Integrity', icon: 'integrity' }
	] as const;
</script>

<script lang="ts">
	import NavTabs from '#lib/components/layout/NavTabs.svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { formatCount } from '#lib/format.js';
	import { navCounts } from '#lib/stores/navCounts.svelte.js';
	import Icon from '#lib/icons/Icon.svelte';
</script>

<nav class="mb-3" aria-label="ROM Tools">
	<NavTabs
		tabs={romTools.map((tool) => ({ ...tool, id: tool.href, href: resolve(tool.href) }))}
		active={romTools.find((tool) => page.url.pathname === resolve(tool.href))?.href ?? null}
		linkClass="d-flex align-items-center gap-1"
	>
		{#snippet content(tab)}
			<Icon name={tab.icon!} />
			{tab.label}
			{#if tab.id === '/review' && navCounts.review}
				<span class="badge rounded-pill text-bg-danger">
					{formatCount(navCounts.review)}<span class="visually-hidden">&#32;pending</span>
				</span>
			{/if}
			{#if tab.id === '/deleted' && navCounts.deleted}
				<span class="badge rounded-pill text-bg-secondary">
					{formatCount(navCounts.deleted)}<span class="visually-hidden">&#32;deleted</span>
				</span>
			{/if}
		{/snippet}
	</NavTabs>
</nav>
