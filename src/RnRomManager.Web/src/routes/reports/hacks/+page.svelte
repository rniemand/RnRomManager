<script lang="ts">
	import SortHeader from '#lib/components/common/SortHeader.svelte';
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { pageParam } from '#lib/urlState.js';
	import { parseDir } from '#lib/sort.js';
	import type { SortDir } from '#lib/enums/index.js';
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/types/index.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import { formatCount } from '#lib/format.js';

	/** Release editing B13: Games With Hacks. The system, sort and page are URL state (`?system=&sort=&dir=&page=`). */
	interface HackedGame {
		id: string;
		displayTitle: string;
		system: string;
		systemName: string;
		hackCount: number;
		releaseCount: number;
		bytes: number;
	}

	type Sort = 'name' | 'system' | 'hacks' | 'releases' | 'size';
	interface View {
		system: string;
		sort: Sort;
		dir: SortDir;
		page: number;
	}

	const pageSize = 50;
	const defaultSort: Sort = 'hacks';
	/** Text columns start ascending, numbers descending. */
	const defaultDir = (sort: Sort): SortDir =>
		sort === 'name' || sort === 'system' ? 'asc' : 'desc';

	const columns: { sort: Sort; label: string; numeric: boolean }[] = [
		{ sort: 'name', label: 'Game', numeric: false },
		{ sort: 'system', label: 'System', numeric: false },
		{ sort: 'hacks', label: 'Hacks', numeric: true },
		{ sort: 'releases', label: 'Releases', numeric: true },
		{ sort: 'size', label: 'Size', numeric: true }
	];

	const system = $derived(page.url.searchParams.get('system') ?? '');
	const pageNumber = $derived(pageParam(page.url.searchParams, 'page', 'digits'));
	const sort = $derived(
		columns.find((c) => c.sort === page.url.searchParams.get('sort'))?.sort ?? defaultSort
	);
	const dir = $derived(parseDir(page.url.searchParams.get('dir'), defaultDir(sort)));

	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: View): void {
		const query = [
			next.system && `system=${encodeURIComponent(next.system)}`,
			next.sort !== defaultSort && `sort=${next.sort}`,
			next.dir !== defaultDir(next.sort) && `dir=${next.dir}`,
			next.page !== 1 && `page=${next.page}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/hacks')}${query ? `?${query}` : ''}`, {
			replace: true,
			reset: false
		});
	}

	const view = $derived<View>({ system, sort, dir, page: pageNumber });

	/** The active header flips its direction; another sorts in its default direction, from page 1. */
	function sortBy(key: Sort): void {
		show({
			...view,
			sort: key,
			dir: key === sort ? (dir === 'asc' ? 'desc' : 'asc') : defaultDir(key),
			page: 1
		});
	}

	const report = useAbortableGet<Page<HackedGame>>(
		() => {
			let url = `/api/reports/hacks?sort=${sort}&dir=${dir}&page=${pageNumber}&pageSize=${pageSize}`;
			if (system) url += `&system=${encodeURIComponent(system)}`;
			return url;
		},
		// A page past the last one goes to the last page instead.
		(p) => show({ ...view, page: pageCount(p.total, pageSize) })
	);
	const result = $derived(report.result);
	const error = $derived(report.error);
</script>

<svelte:head><title>Games With Hacks · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<FilterDropdown
			label="System"
			options={systemFilterOptions(systems, view.system || null)}
			value={view.system || null}
			all={{ label: 'All systems', sum: false }}
			filterPlaceholder="Filter systems…"
			onselect={(v) => show({ ...view, system: v ?? '', page: 1 })}
		/>
	</div>
</div>

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
				<EmptyState message="No games have hacked releases." />
			{:else}
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							<SortHeader
								columns={columns.map((c) => ({ ...c, class: c.numeric ? 'text-end' : undefined }))}
								{sort}
								{dir}
								onsort={sortBy}
							/>
						</tr>
					</thead>
					<tbody>
						{#each result.items as item (item.id)}
							<tr>
								<td>
									<a href="{resolve('/games/[id]', { id: item.id })}?tab=releases"
										>{item.displayTitle}</a
									>
								</td>
								<td><SystemBadge system={item.system} name={item.systemName} /></td>
								<td class="text-end text-nowrap">{item.hackCount}</td>
								<td class="text-end text-nowrap">{item.releaseCount}</td>
								<td class="text-end text-nowrap"><FileSize bytes={item.bytes} /></td>
							</tr>
						{/each}
					</tbody>
				</table>
			{/if}
		</div>
	</div>
	<Pagination
		page={pageNumber}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => show({ ...view, page: p })}
	/>
{/if}
