<script lang="ts">
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { debounced } from '#lib/debounce.js';
	import { pageParam } from '#lib/urlState.js';
	import { parseDir } from '#lib/sort.js';
	import type { SortDir } from '#lib/enums/index.js';
	import { onDestroy, untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { MissingIgdbItem, Page, SystemInfo } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';

	/**
	 * Missing IGDB IDs R3: games not linked to IGDB (or marked Not on IGDB), copying Missing Metadata's page. Filters, sort
	 * and page are URL state under the API's names (`?system=&status=&q=&sort=&dir=&page=`), defaults left out.
	 */
	type Status = 'missing' | 'not-on-igdb';
	type Sort = 'name' | 'system' | 'year';
	interface View {
		system: string;
		status: Status;
		q: string;
		sort: Sort;
		dir: SortDir;
		page: number;
	}

	const pageSize = 50;
	const statusOptions: { value: Status; label: string }[] = [
		{ value: 'missing', label: 'Not linked' },
		{ value: 'not-on-igdb', label: 'Not on IGDB' }
	];
	const columns: { sort: Sort; label: string }[] = [
		{ sort: 'name', label: 'Game' },
		{ sort: 'system', label: 'System' },
		{ sort: 'year', label: 'Year' }
	];

	const params = $derived(page.url.searchParams);
	const view = $derived.by<View>(() => {
		const sort = params.get('sort');
		return {
			system: params.get('system') ?? '',
			status: params.get('status') === 'not-on-igdb' ? 'not-on-igdb' : 'missing',
			q: (params.get('q') ?? '').trim(),
			sort: sort === 'system' || sort === 'year' ? sort : 'name',
			dir: parseDir(params.get('dir'), 'asc'),
			page: pageParam(params, 'page', 'digits')
		};
	});
	/** System or Title set; Show isn't counted for the empty text, since each status has its own unfiltered text. */
	const filtered = $derived(!!(view.system || view.q));
	/** Clear resets Show too, as Missing Metadata's Clear resets Missing. */
	const clearable = $derived(filtered || view.status !== 'missing');

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: View): void {
		const query = [
			next.system && `system=${encodeURIComponent(next.system)}`,
			next.status !== 'missing' && `status=${next.status}`,
			next.q && `q=${encodeURIComponent(next.q)}`,
			next.sort !== 'name' && `sort=${next.sort}`,
			next.dir !== 'asc' && `dir=${next.dir}`,
			next.page !== 1 && `page=${next.page}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/missing-igdb-ids')}${query ? `?${query}` : ''}`, {
			replace: true,
			reset: false
		});
	}

	/** The active header flips its direction; another sorts ascending, from page 1. */
	function sortBy(key: Sort): void {
		show({
			...view,
			sort: key,
			dir: key === view.sort && view.dir === 'asc' ? 'desc' : 'asc',
			page: 1
		});
	}

	let search = $state('');
	onDestroy(() => onSearchInput.cancel());

	const onSearchInput = debounced(() => {
		const q = search.trim();
		if (q !== view.q) show({ ...view, q, page: 1 });
	}, 300);

	// URL → search box (restore, Clear).
	$effect(() => {
		const q = view.q;
		untrack(() => {
			if (search.trim() !== q) search = q;
		});
	});

	function clear(): void {
		onSearchInput.cancel();
		search = '';
		show({ ...view, system: '', status: 'missing', q: '', page: 1 });
	}

	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	const report = useAbortableGet<Page<MissingIgdbItem>>(() => {
		let url = `/api/reports/missing-igdb-ids?status=${view.status}&sort=${view.sort}&dir=${view.dir}&page=${view.page}&pageSize=${pageSize}`;
		if (view.system) url += `&system=${encodeURIComponent(view.system)}`;
		if (view.q) url += `&q=${encodeURIComponent(view.q)}`;
		return url;
	});
	const result = $derived(report.result);
	const error = $derived(report.error);
</script>

<svelte:head><title>Missing IGDB IDs · RnRomManager</title></svelte:head>

<AdminOnly>
	<form
		class="d-flex flex-wrap align-items-center gap-2 mb-3"
		role="search"
		onsubmit={(e) => e.preventDefault()}
	>
		<div class="input-group input-group-sm flex-grow-1 flex-md-grow-0 missing-metadata-title">
			<label class="input-group-text" for="missing-igdb-q">Title</label>
			<input
				id="missing-igdb-q"
				type="search"
				class="form-control"
				maxlength="100"
				bind:value={search}
				oninput={onSearchInput}
			/>
		</div>
		<FilterDropdown
			label="System"
			options={systemFilterOptions(systems, view.system || null)}
			value={view.system || null}
			all={{ label: 'All systems', sum: false }}
			filterPlaceholder="Filter systems…"
			onselect={(v) => show({ ...view, system: v ?? '', page: 1 })}
		/>
		<div class="input-group input-group-sm w-auto">
			<label class="input-group-text" for="missing-igdb-status">Show</label>
			<select
				id="missing-igdb-status"
				class="form-select w-auto"
				value={view.status}
				onchange={(e) => show({ ...view, status: e.currentTarget.value as Status, page: 1 })}
			>
				{#each statusOptions as option (option.value)}
					<option value={option.value}>{option.label}</option>
				{/each}
			</select>
		</div>
		<button
			type="button"
			class="btn btn-sm btn-outline-secondary"
			disabled={!clearable}
			onclick={clear}>Clear</button
		>
	</form>

	{#if error}
		<ErrorAlert {error} onretry={() => report.reload()} />
	{:else if !result}
		<Loading />
	{:else}
		{#if result.total > 0}
			<p class="small text-body-secondary mb-2">
				{formatCount(result.total)}
				{result.total === 1 ? 'game' : 'games'}
			</p>
		{/if}
		<div class="card">
			<div class="table-responsive">
				{#if result.total === 0}
					<EmptyState
						message={filtered
							? 'No games match these filters.'
							: view.status === 'missing'
								? 'Every game is linked to IGDB.'
								: 'No games are marked as not on IGDB.'}
					/>
				{:else}
					<table class="table table-sm table-striped table-hover align-middle mb-0">
						<thead class="table-dark">
							<tr>
								{#each columns as column (column.label)}
									{@const key = column.sort}
									<th
										scope="col"
										aria-sort={key === view.sort
											? view.dir === 'asc'
												? 'ascending'
												: 'descending'
											: undefined}
									>
										<button
											type="button"
											class="btn btn-link p-0 text-reset text-decoration-none fw-semibold"
											onclick={() => sortBy(key)}
											>{column.label}
											{#if key === view.sort}<Icon
													name={view.dir === 'asc' ? 'sort-asc' : 'sort-desc'}
												/>{/if}</button
										>
									</th>
								{/each}
							</tr>
						</thead>
						<tbody>
							{#each result.items as item (item.id)}
								<tr>
									<td>
										<!-- Unlinked games open Lookup Info, with Next missing IGDB ID (as Missing Metadata's links). -->
										<a
											href="{resolve('/games/[id]', { id: item.id })}{view.status === 'missing'
												? '?from=missing-igdb'
												: ''}">{item.displayTitle}</a
										>
									</td>
									<td><SystemBadge system={item.system} name={item.systemName} /></td>
									<td>{item.releaseYear ?? '—'}</td>
								</tr>
							{/each}
						</tbody>
					</table>
				{/if}
			</div>
		</div>
		<Pagination
			page={view.page}
			pages={pageCount(result.total, pageSize)}
			onpage={(p) => show({ ...view, page: p })}
		/>
	{/if}
</AdminOnly>
