<script lang="ts">
	import { untrack } from 'svelte';
	import { auth } from '../stores/auth.svelte';
	import { resolve } from '$app/paths';
	import type { DiscoveryGroup, Page, DiscoveryState } from '#lib/types/index.js';
	import { defaultDir, discoveryApi, discoverySearch, discoveryText, maxFind } from '../discovery';
	import type { GroupSort, SortDir } from '#lib/enums/index.js';
	import { formatCount } from '../format';
	import { pageCount, readPageSize, writePageSize } from '../pageSize';
	import EmptyState from './EmptyState.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import Loading from './Loading.svelte';
	import Pagination from './Pagination.svelte';
	import Icon from './Icon.svelte';
	import GroupLogo from './GroupLogo.svelte';
	import LogoModal from './LogoModal.svelte';

	/**
	 * Discovery B2: one page of a kind's live groups (publishers, developers or genres across systems), each a link selecting it.
	 * The page number lives in the URL (`groupPage`); the rows per page are remembered in this browser.
	 */
	interface Props {
		view: DiscoveryState;
		/** Go to another group page, keeping the selection. */
		onpage: (groupPage: number) => void;
		/** Called with the selected group whenever a loaded page holds it (for the games heading). */
		onselected?: (group: DiscoveryGroup) => void;
		/** B16: a new search (trimmed, '' for none); the page writes it to the URL with group page 1. */
		onfind?: (find: string) => void;
		/** G4: a header click; the page writes the sort to the URL with group page 1. `view.sort`/`view.dir` are the current one. */
		onsort?: (sort: GroupSort, dir: SortDir) => void;
		/** G7: Clear; the page drops `find`, `sort` and `dir` from the URL. */
		onclear?: () => void;
	}

	let { view, onpage, onselected, onfind, onsort, onclear }: Props = $props();

	const sort = $derived<GroupSort>(view.sort ?? 'name');
	const dir = $derived<SortDir>(view.dir ?? defaultDir(sort));
	const columns: readonly { sort: GroupSort; label: string; numeric: boolean }[] = [
		{ sort: 'name', label: 'Name', numeric: false },
		{ sort: 'games', label: 'Games', numeric: true }
	];

	/** G4: the active header flips its direction; the other sorts in its own default. */
	function sortBy(key: GroupSort): void {
		onsort?.(key, key === sort ? (dir === 'asc' ? 'desc' : 'asc') : defaultDir(key));
	}

	/** G7: cancels a pending search and empties the box; the page clears the URL. */
	function clear(): void {
		clearTimeout(findTimer);
		findText = '';
		onclear?.();
	}

	/** B16: the search box; typing is debounced, and the URL is the source of truth (Back/Forward). */
	let findText = $state(untrack(() => view.find ?? ''));
	let findTimer: ReturnType<typeof setTimeout> | undefined;
	function onFindInput(): void {
		clearTimeout(findTimer);
		findTimer = setTimeout(() => {
			if (findText.trim() !== (view.find ?? '')) onfind?.(findText.trim());
		}, 300);
	}
	// A pending search is dropped on unmount and when the kind changes (the list stays mounted between tabs).
	$effect(() => {
		void view.kind;
		return () => clearTimeout(findTimer);
	});
	$effect(() => {
		const current = view.find ?? '';
		untrack(() => {
			if (findText.trim() !== current) findText = current;
		});
	});

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

	const text = $derived(discoveryText[view.kind]);
	const noun = $derived(text.plural);

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
		const params = {
			page: view.groupPage,
			pageSize,
			q: view.find,
			sort: view.sort,
			dir: view.dir
		};
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

<!-- B16: outside the loading branch, so it keeps focus while results reload. -->
<div class="d-flex gap-2 mb-2">
	<label class="visually-hidden" for="find-{view.kind}">Search {noun}</label>
	<input
		id="find-{view.kind}"
		type="search"
		class="form-control form-control-sm flex-grow-1"
		placeholder="Search {noun}"
		maxlength={maxFind}
		bind:value={findText}
		oninput={onFindInput}
	/>
	{#if view.find || sort !== 'name' || dir !== 'asc'}
		<button type="button" class="btn btn-sm btn-outline-secondary text-nowrap" onclick={clear}
			><Icon name="clear" /> Clear</button
		>
	{/if}
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading {noun}…" />
{:else if result.items.length === 0}
	{#if view.find && result.total === 0}
		<EmptyState message="No {noun} match “{view.find}”." />
	{:else if result.total === 0}
		<EmptyState message="No games have {noun} yet." icon="collections" />
	{:else}
		<EmptyState message="There are no {noun} on this page." />
	{/if}
{:else}
	<div class="card">
		<div class="table-responsive">
			<table
				class="table table-sm table-striped table-hover align-middle mb-0"
				aria-label={text.label}
			>
				<thead class="table-dark">
					<tr>
						{#each columns as column (column.sort)}
							<th
								scope="col"
								class={column.numeric ? 'text-end text-nowrap' : undefined}
								aria-sort={column.sort === sort
									? dir === 'asc'
										? 'ascending'
										: 'descending'
									: undefined}
							>
								<button
									type="button"
									class="btn btn-link p-0 text-reset text-decoration-none fw-semibold"
									onclick={() => sortBy(column.sort)}
									>{column.label}
									{#if column.sort === sort}<Icon
											name={dir === 'asc' ? 'sort-asc' : 'sort-desc'}
										/>{/if}</button
								>
							</th>
						{/each}
						{#if auth.isAdmin}
							<th scope="col"><span class="visually-hidden">Actions</span></th>
						{/if}
					</tr>
				</thead>
				<tbody>
					{#each result.items as group (group.key)}
						<tr class:table-primary={group.key === view.key}>
							<td class="w-100 text-truncate" style="max-width: 0">
								<a
									class="text-decoration-none d-flex align-items-center gap-2 min-w-0"
									class:text-reset={group.key === view.key}
									aria-current={group.key === view.key ? 'true' : undefined}
									href="{resolve('/collections')}{selection(group)}"
								>
									<GroupLogo kind={view.kind} item={group} />
									<span class="text-truncate" title={group.name}>{group.name}</span>
								</a>
							</td>
							<td class="text-end text-nowrap">{formatCount(group.gameCount)}</td>
							{#if auth.isAdmin}
								<td>
									<button
										type="button"
										class="btn btn-link btn-sm p-0"
										aria-label="Change logo for {group.name}"
										title="Change logo"
										onclick={() => {
											editing = group;
											logoOpen = true;
										}}><Icon name="edit" /></button
									>
								</td>
							{/if}
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}

{#if result}
	<Pagination
		page={view.groupPage}
		pages={pageCount(result.total, pageSize)}
		{onpage}
		{pageSize}
		onpagesize={setPageSize}
		label={text.pager}
	/>
{/if}

<LogoModal bind:open={logoOpen} kind={view.kind} item={editing} onchanged={() => reload++} />
