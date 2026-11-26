<script lang="ts">
	import Select from '#lib/components/forms/Select.svelte';
	import SearchInput from '#lib/components/forms/SearchInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import SortHeader from '#lib/components/common/SortHeader.svelte';
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
	import type { MissingMetadataItem, Page, SystemInfo } from '#lib/types/index.js';
	import AdminOnly from '#lib/components/common/AdminOnly.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';

	/**
	 * Missing Metadata M4, M6: games with no year, developer or publisher. Filters, sort and page are URL state under the
	 * API's names (`?system=&missing=&q=&sort=&dir=&page=`), defaults left out.
	 */
	type Missing = 'any' | 'year' | 'developer' | 'publisher';
	type Sort = 'name' | 'system' | 'year';
	interface View {
		system: string;
		missing: Missing;
		q: string;
		sort: Sort;
		dir: SortDir;
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
		return {
			system: params.get('system') ?? '',
			missing: missingOptions.some((o) => o.value === missing) ? (missing as Missing) : 'any',
			q: (params.get('q') ?? '').trim(),
			sort: sort === 'system' || sort === 'year' ? sort : 'name',
			dir: parseDir(params.get('dir'), 'asc'),
			page: pageParam(params, 'page', 'digits')
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
		show({ ...view, system: '', missing: 'any', q: '', page: 1 });
	}

	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	const report = useAbortableGet<Page<MissingMetadataItem>>(() => {
		let url = `/api/reports/missing-metadata?missing=${view.missing}&sort=${view.sort}&dir=${view.dir}&page=${view.page}&pageSize=${pageSize}`;
		if (view.system) url += `&system=${encodeURIComponent(view.system)}`;
		if (view.q) url += `&q=${encodeURIComponent(view.q)}`;
		return url;
	});
	const result = $derived(report.result);
	const error = $derived(report.error);
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
			<SearchInput
				id="missing-metadata-q"
				maxlength={100}
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
			<Select
				class="w-auto"
				id="missing-metadata-missing"
				value={view.missing}
				onchange={(e) => show({ ...view, missing: e.currentTarget.value as Missing, page: 1 })}
				>{#each missingOptions as option (option.value)}
					<option value={option.value}>{option.label}</option>
				{/each}</Select
			>
		</div>
		<Button variant="secondary" outline size="sm" disabled={!filtered} onclick={clear}>Clear</Button
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
		<DataTable
			items={result.items}
			key={(item) => item.id}
			empty={filtered
				? 'No games match these filters.'
				: 'Every game has its year, developer and publisher.'}
			emptyInCard
		>
			{#snippet head()}
				<tr>
					<SortHeader {columns} sort={view.sort} dir={view.dir} onsort={sortBy} />
				</tr>
			{/snippet}
			{#snippet row(item)}
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
			{/snippet}
		</DataTable>
		<Pagination
			page={view.page}
			pages={pageCount(result.total, pageSize)}
			onpage={(p) => show({ ...view, page: p })}
		/>
	{/if}
</AdminOnly>
