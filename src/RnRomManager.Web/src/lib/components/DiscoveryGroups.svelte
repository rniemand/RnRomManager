<script lang="ts">
	import { auth } from '../stores/auth.svelte';
	import { resolve } from '$app/paths';
	import type { DiscoveryGroup, Page } from '../api/types';
	import { discoveryApi, discoverySearch, type DiscoveryState } from '../discovery';
	import { pageCount, readPageSize, writePageSize } from '../pageSize';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Loading from './Loading.svelte';
	import Pagination from './Pagination.svelte';
	import Icon from './Icon.svelte';
	import GroupLogo from './GroupLogo.svelte';
	import LogoModal from './LogoModal.svelte';

	/**
	 * Discovery B2: one page of a kind's live groups (publishers or genres across systems), each a link selecting it.
	 * The page number lives in the URL (`groupPage`); the rows per page are remembered in this browser.
	 */
	interface Props {
		view: DiscoveryState;
		/** Go to another group page, keeping the selection. */
		onpage: (groupPage: number) => void;
		/** Called with the selected group whenever a loaded page holds it (for the games heading). */
		onselected?: (group: DiscoveryGroup) => void;
	}

	let { view, onpage, onselected }: Props = $props();

	$effect(() => {
		const selected = result?.items.find((g) => g.key === view.key);
		if (selected) onselected?.(selected);
	});

	const sizeKey = 'discoveryGroups';
	let pageSize = $state(readPageSize(sizeKey));
	let result = $state<Page<DiscoveryGroup> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	/** B19: the group whose logo is being changed. */
	let editing = $state<DiscoveryGroup | null>(null);
	let logoOpen = $state(false);

	const noun = $derived(view.kind === 'publishers' ? 'publishers' : 'genres');

	function setPageSize(size: number): void {
		pageSize = size;
		writePageSize(sizeKey, size);
		onpage(1);
	}

	/** A group's selection query: same kind and group page, its first game page. */
	const selection = (group: DiscoveryGroup) =>
		discoverySearch({ ...view, key: group.key, gamePage: 1 });

	$effect(() => {
		const kind = view.kind;
		const params = { page: view.groupPage, pageSize };
		void reload;
		// A response for an earlier kind or page never replaces the current one.
		let stale = false;
		error = null;
		result = null;
		discoveryApi.groups(kind, params).then(
			(page) => {
				if (!stale) result = page;
			},
			(e) => {
				if (!stale) error = e;
			}
		);
		return () => (stale = true);
	});
</script>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading {noun}…" />
{:else if result.items.length === 0}
	{#if result.total === 0}
		<EmptyState message="No games have {noun} yet." icon="collections" />
	{:else}
		<EmptyState message="There are no {noun} on this page." />
	{/if}
{:else}
	<ul class="list-group" aria-label={view.kind === 'publishers' ? 'Publishers' : 'Genres'}>
		{#each result.items as group (group.key)}
			<li class="list-group-item p-0 d-flex align-items-stretch">
				<a
					class="list-group-item-action flex-grow-1 min-w-0 d-flex align-items-center justify-content-between gap-2 px-3 py-2 text-decoration-none"
					class:active={group.key === view.key}
					aria-current={group.key === view.key ? 'true' : undefined}
					href="{resolve('/collections')}{selection(group)}"
				>
					<span class="d-flex align-items-center gap-2 min-w-0">
						<GroupLogo kind={view.kind} item={group} />
						<span class="text-truncate" title={group.name}>{group.name}</span>
					</span>
					<span class="badge text-bg-secondary rounded-pill"
						>{group.gameCount} {group.gameCount === 1 ? 'game' : 'games'}</span
					>
				</a>
				{#if auth.isAdmin}
					<!-- B19: beside the link, not in it (no button inside a link). -->
					<button
						type="button"
						class="btn btn-link btn-sm border-start rounded-0 px-2"
						aria-label="Change logo for {group.name}"
						title="Change logo"
						onclick={() => {
							editing = group;
							logoOpen = true;
						}}><Icon name="edit" /></button
					>
				{/if}
			</li>
		{/each}
	</ul>
{/if}

{#if result}
	<Pagination
		page={view.groupPage}
		pages={pageCount(result.total, pageSize)}
		{onpage}
		{pageSize}
		onpagesize={setPageSize}
		label="{view.kind === 'publishers' ? 'Publisher' : 'Genre'} pages"
	/>
{/if}

<LogoModal bind:open={logoOpen} kind={view.kind} item={editing} onchanged={() => reload++} />

<style>
	.min-w-0 {
		min-width: 0;
	}
</style>
