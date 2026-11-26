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
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { formatCount } from '#lib/format.js';
	import { navCounts } from '#lib/stores/navCounts.svelte.js';
	import Icon from '#lib/icons/Icon.svelte';
</script>

<nav class="mb-3" aria-label="ROM Tools">
	<ul class="nav nav-tabs" use:scrollActiveTab>
		{#each romTools as tool (tool.href)}
			{@const active = page.url.pathname === resolve(tool.href)}
			<li class="nav-item">
				<a
					class="nav-link d-flex align-items-center gap-1"
					class:active
					aria-current={active ? 'page' : undefined}
					href={resolve(tool.href)}
				>
					<Icon name={tool.icon} />
					{tool.label}
					{#if tool.href === '/review' && navCounts.review}
						<span class="badge rounded-pill text-bg-danger">
							{formatCount(navCounts.review)}<span class="visually-hidden">&#32;pending</span>
						</span>
					{/if}
					{#if tool.href === '/deleted' && navCounts.deleted}
						<span class="badge rounded-pill text-bg-secondary">
							{formatCount(navCounts.deleted)}<span class="visually-hidden">&#32;deleted</span>
						</span>
					{/if}
				</a>
			</li>
		{/each}
	</ul>
</nav>
