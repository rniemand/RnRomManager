<script lang="ts">
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/types/index.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import FilterDropdown from '#lib/components/FilterDropdown.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';
	import { formatCount } from '#lib/format.js';

	/**
	 * Multiple Releases B14, B18–B20. The system (release editing B12), threshold, sort and page are URL state
	 * (`?system=&min=&sort=&dir=&page=`).
	 */
	interface MultipleRelease {
		id: string;
		displayTitle: string;
		system: string;
		systemName: string;
		releaseCount: number;
		updateCount: number;
		bytes: number;
	}

	type Sort = 'name' | 'system' | 'releases' | 'updates' | 'size';
	type Dir = 'asc' | 'desc';
	interface View {
		system: string;
		min: number;
		sort: Sort;
		dir: Dir;
		page: number;
	}

	const pageSize = 50;
	const defaultMin = 2;
	const defaultSort: Sort = 'releases';
	/** B16: text columns start ascending, numbers descending. */
	const defaultDir = (sort: Sort): Dir => (sort === 'name' || sort === 'system' ? 'asc' : 'desc');

	/** B19: the headers, each a sort button. */
	const columns: { sort: Sort; label: string; numeric: boolean }[] = [
		{ sort: 'name', label: 'Game', numeric: false },
		{ sort: 'system', label: 'System', numeric: false },
		{ sort: 'releases', label: 'Releases', numeric: true },
		{ sort: 'updates', label: 'Updates', numeric: true },
		{ sort: 'size', label: 'Size', numeric: true }
	];

	/** A whole number in [lo, hi], else null (B18: bad URL values fall back to the default). */
	function whole(value: string | null, lo: number, hi: number): number | null {
		if (value === null || !/^\d+$/.test(value)) return null;
		const n = Number(value);
		return n >= lo && n <= hi ? n : null;
	}

	const system = $derived(page.url.searchParams.get('system') ?? '');
	const min = $derived(whole(page.url.searchParams.get('min'), 2, 10000) ?? defaultMin);
	const pageNumber = $derived(
		whole(page.url.searchParams.get('page'), 1, Number.MAX_SAFE_INTEGER) ?? 1
	);
	// B18: exact lowercase matches only, as libraryQuery.ts.
	const sort = $derived(
		columns.find((c) => c.sort === page.url.searchParams.get('sort'))?.sort ?? defaultSort
	);
	const dir = $derived.by<Dir>(() => {
		const value = page.url.searchParams.get('dir');
		return value === 'asc' || value === 'desc' ? value : defaultDir(sort);
	});

	/** The last response's `maxReleases`; null until the first one arrives. */
	let maxReleases = $state<number | null>(null);
	let result = $state<(Page<MultipleRelease> & { maxReleases: number }) | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	/** 2…max(maxReleases, 2), plus a URL `min` above that; just the current `min` before the first response. */
	const options = $derived.by(() => {
		if (maxReleases === null) return [min];
		const top = Math.max(maxReleases, defaultMin);
		const list = Array.from({ length: top - 1 }, (_, i) => i + defaultMin);
		return min > top ? [...list, min] : list;
	});

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: View): void {
		const query = [
			next.system && `system=${encodeURIComponent(next.system)}`,
			next.min !== defaultMin && `min=${next.min}`,
			next.sort !== defaultSort && `sort=${next.sort}`,
			next.dir !== defaultDir(next.sort) && `dir=${next.dir}`,
			next.page !== 1 && `page=${next.page}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(`${resolve('/reports/multiple-releases')}${query ? `?${query}` : ''}`, {
			replace: true,
			reset: false
		});
	}

	const view = $derived<View>({ system, min, sort, dir, page: pageNumber });

	/** B19: the active header flips its direction; another sorts in its default direction, from page 1. */
	function sortBy(key: Sort): void {
		show({
			...view,
			sort: key,
			dir: key === sort ? (dir === 'asc' ? 'desc' : 'asc') : defaultDir(key),
			page: 1
		});
	}

	$effect(() => {
		let url = `/api/reports/multiple-releases?min=${min}&sort=${sort}&dir=${dir}&page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		const current = view;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api
			.get<Page<MultipleRelease> & { maxReleases: number }>(url, { signal: controller.signal })
			.then(
				(p) => {
					maxReleases = p.maxReleases;
					// B20: a page past the last one goes to the last page instead.
					if (p.items.length === 0 && p.total > 0)
						show({ ...current, page: pageCount(p.total, pageSize) });
					else result = p;
				},
				(e) => {
					if (!controller.signal.aborted) error = e;
				}
			);
		return () => controller.abort();
	});
</script>

<svelte:head><title>Multiple Releases · RnRomManager</title></svelte:head>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<FilterDropdown
		label="System"
		options={systemFilterOptions(systems, view.system || null)}
		value={view.system || null}
		all={{ label: 'All systems', sum: false }}
		filterPlaceholder="Filter systems…"
		onselect={(v) => show({ ...view, system: v ?? '', page: 1 })}
	/>
	<div class="input-group input-group-sm w-auto">
		<label class="input-group-text" for="multiple-releases-min">Minimum releases</label>
		<select
			id="multiple-releases-min"
			class="form-select w-auto"
			value={String(min)}
			disabled={maxReleases === null}
			onchange={(e) => show({ ...view, min: Number(e.currentTarget.value), page: 1 })}
		>
			{#each options as option (option)}
				<option value={String(option)}>{option}</option>
			{/each}
		</select>
	</div>
</div>

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
				<EmptyState message="No games have {min} or more releases." />
			{:else}
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							{#each columns as column (column.sort)}
								<th
									scope="col"
									class={column.numeric ? 'text-end' : undefined}
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
						</tr>
					</thead>
					<tbody>
						{#each result.items as item (item.id)}
							<tr>
								<td><a href={resolve('/games/[id]', { id: item.id })}>{item.displayTitle}</a></td>
								<td><SystemBadge system={item.system} name={item.systemName} /></td>
								<td class="text-end text-nowrap">{item.releaseCount}</td>
								<td class="text-end text-nowrap">{item.updateCount}</td>
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
