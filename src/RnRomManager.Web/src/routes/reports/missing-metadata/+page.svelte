<script lang="ts">
	import { onDestroy, untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { MissingMetadataItem, Page, SystemInfo } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/AdminOnly.svelte';
	import FilterDropdown from '#lib/components/FilterDropdown.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';

	/**
	 * Missing Metadata M4, M6: games with no year, developer or publisher. Filters, sort and page are URL state under the
	 * API's names (`?system=&missing=&q=&sort=&dir=&page=`), defaults left out.
	 */
	type Missing = 'any' | 'year' | 'developer' | 'publisher';
	type Sort = 'name' | 'system' | 'year';
	type Dir = 'asc' | 'desc';
	interface View {
		system: string;
		missing: Missing;
		q: string;
		sort: Sort;
		dir: Dir;
		page: number;
	}

	const pageSize = 50;
	const missingOptions: { value: Missing; label: string }[] = [
		{ value: 'any', label: 'Any' },
		{ value: 'year', label: 'Year' },
		{ value: 'developer', label: 'Developer' },
		{ value: 'publisher', label: 'Publisher' }
	];
	const columns: { sort: Sort | null; label: string }[] = [
		{ sort: 'name', label: 'Game' },
		{ sort: 'system', label: 'System' },
		{ sort: 'year', label: 'Year' },
		{ sort: null, label: 'Developer' },
		{ sort: null, label: 'Publisher' }
	];

	const params = $derived(page.url.searchParams);
	const view = $derived.by<View>(() => {
		const missing = params.get('missing');
		const sort = params.get('sort');
		const dir = params.get('dir');
		const p = params.get('page');
		return {
			system: params.get('system') ?? '',
			missing: missingOptions.some((o) => o.value === missing) ? (missing as Missing) : 'any',
			q: (params.get('q') ?? '').trim(),
			sort: sort === 'system' || sort === 'year' ? sort : 'name',
			dir: dir === 'desc' ? 'desc' : 'asc',
			page: p !== null && /^\d+$/.test(p) && Number(p) >= 1 ? Number(p) : 1
		};
	});
	const filtered = $derived(!!(view.system || view.missing !== 'any' || view.q));

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: View): void {
		const query = [
			next.system && `system=${encodeURIComponent(next.system)}`,
			next.missing !== 'any' && `missing=${next.missing}`,
			next.q && `q=${encodeURIComponent(next.q)}`,
			next.sort !== 'name' && `sort=${next.sort}`,
			next.dir !== 'asc' && `dir=${next.dir}`,
			next.page !== 1 && `page=${next.page}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/missing-metadata')}${query ? `?${query}` : ''}`, {
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
	let searchTimer: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(searchTimer));

	function onSearchInput(): void {
		clearTimeout(searchTimer);
		searchTimer = setTimeout(() => {
			const q = search.trim();
			if (q !== view.q) show({ ...view, q, page: 1 });
		}, 300);
	}

	// URL → search box (restore, Clear).
	$effect(() => {
		const q = view.q;
		untrack(() => {
			if (search.trim() !== q) search = q;
		});
	});

	function clear(): void {
		clearTimeout(searchTimer);
		search = '';
		show({ ...view, system: '', missing: 'any', q: '', page: 1 });
	}

	let systems = $state<SystemInfo[]>([]);
	let result = $state<Page<MissingMetadataItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	$effect(() => {
		let url = `/api/reports/missing-metadata?missing=${view.missing}&sort=${view.sort}&dir=${view.dir}&page=${view.page}&pageSize=${pageSize}`;
		if (view.system) url += `&system=${encodeURIComponent(view.system)}`;
		if (view.q) url += `&q=${encodeURIComponent(view.q)}`;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<MissingMetadataItem>>(url, { signal: controller.signal }).then(
			(p) => (result = p),
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Missing Metadata · RnRomManager</title></svelte:head>

{#snippet missingBadge()}<span class="badge text-bg-warning">Missing</span>{/snippet}

{#snippet list(values: string[])}
	{#if values.length === 0}{@render missingBadge()}{:else}{values.join(', ')}{/if}
{/snippet}

<AdminOnly>
	<form
		class="d-flex flex-wrap align-items-center gap-2 mb-3"
		role="search"
		onsubmit={(e) => e.preventDefault()}
	>
		<div class="input-group input-group-sm flex-grow-1 flex-md-grow-0 missing-metadata-title">
			<label class="input-group-text" for="missing-metadata-q">Title</label>
			<input
				id="missing-metadata-q"
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
			<label class="input-group-text" for="missing-metadata-missing">Missing</label>
			<select
				id="missing-metadata-missing"
				class="form-select w-auto"
				value={view.missing}
				onchange={(e) => show({ ...view, missing: e.currentTarget.value as Missing, page: 1 })}
			>
				{#each missingOptions as option (option.value)}
					<option value={option.value}>{option.label}</option>
				{/each}
			</select>
		</div>
		<button
			type="button"
			class="btn btn-sm btn-outline-secondary"
			disabled={!filtered}
			onclick={clear}>Clear</button
		>
	</form>

	{#if error}
		<ErrorAlert {error} onretry={() => reload++} />
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
							: 'Every game has its year, developer and publisher.'}
					/>
				{:else}
					<table class="table table-sm table-striped table-hover align-middle mb-0">
						<thead class="table-dark">
							<tr>
								{#each columns as column (column.label)}
									<th
										scope="col"
										aria-sort={column.sort !== null && column.sort === view.sort
											? view.dir === 'asc'
												? 'ascending'
												: 'descending'
											: undefined}
									>
										{#if column.sort}
											{@const key = column.sort}
											<button
												type="button"
												class="btn btn-link p-0 text-reset text-decoration-none fw-semibold"
												onclick={() => sortBy(key)}
												>{column.label}
												{#if key === view.sort}<Icon
														name={view.dir === 'asc' ? 'sort-asc' : 'sort-desc'}
													/>{/if}</button
											>
										{:else}
											{column.label}
										{/if}
									</th>
								{/each}
							</tr>
						</thead>
						<tbody>
							{#each result.items as item (item.id)}
								<tr>
									<td>
										<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query -->
										<a href="{resolve('/games/[id]', { id: item.id })}?from=missing-metadata"
											>{item.displayTitle}</a
										>
									</td>
									<td><SystemBadge system={item.system} name={item.systemName} /></td>
									<td
										>{#if item.releaseYear === null}{@render missingBadge()}{:else}{item.releaseYear}{/if}</td
									>
									<td>{@render list(item.developers)}</td>
									<td>{@render list(item.publishers)}</td>
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
