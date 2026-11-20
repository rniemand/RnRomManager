<script lang="ts">
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page, SystemInfo } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';

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
	type Dir = 'asc' | 'desc';
	interface View {
		system: string;
		sort: Sort;
		dir: Dir;
		page: number;
	}

	const pageSize = 50;
	const defaultSort: Sort = 'hacks';
	/** Text columns start ascending, numbers descending. */
	const defaultDir = (sort: Sort): Dir => (sort === 'name' || sort === 'system' ? 'asc' : 'desc');

	const columns: { sort: Sort; label: string; numeric: boolean }[] = [
		{ sort: 'name', label: 'Game', numeric: false },
		{ sort: 'system', label: 'System', numeric: false },
		{ sort: 'hacks', label: 'Hacks', numeric: true },
		{ sort: 'releases', label: 'Releases', numeric: true },
		{ sort: 'size', label: 'Size', numeric: true }
	];

	const system = $derived(page.url.searchParams.get('system') ?? '');
	const pageNumber = $derived.by(() => {
		const value = page.url.searchParams.get('page');
		return value !== null && /^\d+$/.test(value) && Number(value) >= 1 ? Number(value) : 1;
	});
	const sort = $derived(
		columns.find((c) => c.sort === page.url.searchParams.get('sort'))?.sort ?? defaultSort
	);
	const dir = $derived.by<Dir>(() => {
		const value = page.url.searchParams.get('dir');
		return value === 'asc' || value === 'desc' ? value : defaultDir(sort);
	});

	let result = $state<Page<HackedGame> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
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

	$effect(() => {
		let url = `/api/reports/hacks?sort=${sort}&dir=${dir}&page=${pageNumber}&pageSize=${pageSize}`;
		if (system) url += `&system=${encodeURIComponent(system)}`;
		const current = view;
		void reload;
		const controller = new AbortController();
		error = null;
		result = null;
		api.get<Page<HackedGame>>(url, { signal: controller.signal }).then(
			(p) => {
				// A page past the last one goes to the last page instead.
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

<svelte:head><title>Games With Hacks · RnRomManager</title></svelte:head>

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<label class="form-label" for="hacks-system">System</label>
		<select
			id="hacks-system"
			class="form-select"
			value={system}
			onchange={(e) => show({ ...view, system: e.currentTarget.value, page: 1 })}
		>
			<option value="">All systems</option>
			{#each systems as s (s.id)}
				<option value={s.id}>{s.displayName}</option>
			{/each}
		</select>
	</div>
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading />
{:else}
	<div class="card">
		<div class="table-responsive">
			{#if result.total === 0}
				<EmptyState message="No games have hacked releases." />
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
