<script lang="ts">
	import { resolve } from '$app/paths';
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type { Page } from '#lib/api/types.js';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Pagination from '#lib/components/Pagination.svelte';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import { pageCount } from '#lib/pageSize.js';

	/** Multiple Releases B14, B18–B20. The threshold and page are URL state (`?min=&page=`). */
	interface MultipleRelease {
		id: string;
		displayTitle: string;
		system: string;
		systemName: string;
		releaseCount: number;
		updateCount: number;
		bytes: number;
	}

	const pageSize = 50;
	const defaultMin = 2;

	/** A whole number in [lo, hi], else null (B18: bad URL values fall back to the default). */
	function whole(value: string | null, lo: number, hi: number): number | null {
		if (value === null || !/^\d+$/.test(value)) return null;
		const n = Number(value);
		return n >= lo && n <= hi ? n : null;
	}

	const min = $derived(whole(page.url.searchParams.get('min'), 2, 10000) ?? defaultMin);
	const pageNumber = $derived(
		whole(page.url.searchParams.get('page'), 1, Number.MAX_SAFE_INTEGER) ?? 1
	);

	/** The last response's `maxReleases`; null until the first one arrives. */
	let maxReleases = $state<number | null>(null);
	let result = $state<(Page<MultipleRelease> & { maxReleases: number }) | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);

	/** 2…max(maxReleases, 2), plus a URL `min` above that; just the current `min` before the first response. */
	const options = $derived.by(() => {
		if (maxReleases === null) return [min];
		const top = Math.max(maxReleases, defaultMin);
		const list = Array.from({ length: top - 1 }, (_, i) => i + defaultMin);
		return min > top ? [...list, min] : list;
	});

	/** Writes the state to the URL, leaving out defaults. */
	function show(next: { min: number; page: number }): void {
		const query = [
			next.min !== defaultMin && `min=${next.min}`,
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

	$effect(() => {
		const url = `/api/reports/multiple-releases?min=${min}&sort=releases&dir=desc&page=${pageNumber}&pageSize=${pageSize}`;
		const current = { min, page: pageNumber };
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

<div class="row g-2 align-items-end mb-3">
	<div class="col-12 col-sm-6 col-md-4">
		<label class="form-label" for="multiple-releases-min">Minimum releases</label>
		<select
			id="multiple-releases-min"
			class="form-select"
			value={String(min)}
			disabled={maxReleases === null}
			onchange={(e) => show({ min: Number(e.currentTarget.value), page: 1 })}
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
	<div class="card">
		<div class="table-responsive">
			{#if result.total === 0}
				<EmptyState message="No games have {min} or more releases." />
			{:else}
				<table class="table table-sm table-striped table-hover align-middle mb-0">
					<thead class="table-dark">
						<tr>
							<th scope="col">Game</th>
							<th scope="col">System</th>
							<th scope="col" class="text-end">Releases</th>
							<th scope="col" class="text-end">Updates</th>
							<th scope="col" class="text-end">Size</th>
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
		onpage={(p) => show({ min, page: p })}
	/>
{/if}
