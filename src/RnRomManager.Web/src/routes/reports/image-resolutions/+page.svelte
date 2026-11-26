<script lang="ts">
	import { useAbortableGet } from '#lib/loadPage.svelte.js';
	import { replaceUrl } from '#lib/urlState.js';
	import { untrack } from 'svelte';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import type {
		ImageResolutionItem,
		Page,
		SystemInfo,
		ImageResolutionFilter
	} from '#lib/types/index.js';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import FilterDropdown from '#lib/components/forms/FilterDropdown.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { formatCount } from '#lib/format.js';
	import {
		imageResolutionApiUrl,
		imageResolutionParams,
		imageResolutionSearch,
		imageTypeLabels,
		parseImageResolutionFilter,
		parseLimit,
		readSavedImageResolutionFilter,
		saveImageResolutionFilter
	} from '#lib/imageResolutions.js';
	import { pageCount } from '#lib/pageSize.js';
	import { systemFilterOptions } from '#lib/systemFilter.js';

	/** Image Resolutions R5–R9: stored images smallest first, filters in the URL and remembered in `localStorage`. */
	const pageSize = 50;

	function go(search: string): void {
		void replaceUrl(`${resolve('/reports/image-resolutions')}${search}`);
	}

	const view = $derived(parseImageResolutionFilter(page.url.searchParams));
	const filtered = $derived(!!(view.system || view.type || view.maxWidth || view.maxHeight));

	/** Any filter change goes back to page 1. */
	function update(changes: Partial<ImageResolutionFilter>): void {
		go(imageResolutionSearch({ ...view, page: 1, ...changes }));
	}

	// First run: a URL without these params restores the saved query; after that every URL state is saved.
	let restoring =
		!imageResolutionParams.some((p) => page.url.searchParams.has(p)) &&
		!!readSavedImageResolutionFilter();
	$effect(() => {
		const search = imageResolutionSearch(view);
		untrack(() => {
			if (restoring) {
				restoring = false;
				go(readSavedImageResolutionFilter());
			} else saveImageResolutionFilter(search);
		});
	});

	/** R6: a value that isn't a whole number in 1–100000 means no limit, and the input is emptied. */
	function onLimit(e: Event & { currentTarget: HTMLInputElement }, key: 'maxWidth' | 'maxHeight') {
		const value = parseLimit(e.currentTarget.value);
		if (value === null) e.currentTarget.value = '';
		if (value !== view[key]) update({ [key]: value });
	}

	let systems = $state<SystemInfo[]>([]);

	$effect(() => {
		api.get<SystemInfo[]>('/api/systems').then(
			(list) => (systems = list),
			() => {} // only feeds the filter
		);
	});

	const report = useAbortableGet<Page<ImageResolutionItem>>(
		() => imageResolutionApiUrl(view, pageSize),
		// As Multiple Releases B20: a page past the last one goes to the last page instead.
		(p) => go(imageResolutionSearch({ ...view, page: pageCount(p.total, pageSize) }))
	);
	const result = $derived(report.result);
	const error = $derived(report.error);
</script>

<svelte:head><title>Image Resolutions · RnRomManager</title></svelte:head>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<FilterDropdown
		label="System"
		options={systemFilterOptions(systems, view.system || null)}
		value={view.system || null}
		all={{ label: 'All systems', sum: false }}
		filterPlaceholder="Filter systems…"
		onselect={(v) => update({ system: v ?? '' })}
	/>
	<div class="input-group input-group-sm w-auto">
		<label class="input-group-text" for="image-resolutions-type">Type</label>
		<select
			id="image-resolutions-type"
			class="form-select w-auto"
			value={view.type}
			onchange={(e) => update({ type: e.currentTarget.value as ImageResolutionFilter['type'] })}
		>
			<option value="">All types</option>
			{#each Object.entries(imageTypeLabels) as [value, label] (value)}
				<option {value}>{label}</option>
			{/each}
		</select>
	</div>
	<div class="input-group input-group-sm w-auto">
		<label class="input-group-text" for="image-resolutions-max-width">Max width</label>
		<input
			id="image-resolutions-max-width"
			type="number"
			class="form-control"
			style="width: 6rem"
			min="1"
			max="100000"
			step="1"
			value={view.maxWidth ?? ''}
			onchange={(e) => onLimit(e, 'maxWidth')}
		/>
		<span class="input-group-text">px</span>
	</div>
	<div class="input-group input-group-sm w-auto">
		<label class="input-group-text" for="image-resolutions-max-height">Max height</label>
		<input
			id="image-resolutions-max-height"
			type="number"
			class="form-control"
			style="width: 6rem"
			min="1"
			max="100000"
			step="1"
			value={view.maxHeight ?? ''}
			onchange={(e) => onLimit(e, 'maxHeight')}
		/>
		<span class="input-group-text">px</span>
	</div>
	<button
		type="button"
		class="btn btn-sm btn-outline-secondary"
		disabled={!filtered}
		onclick={() => go('')}>Clear</button
	>
</div>

{#if error}
	<ErrorAlert {error} onretry={() => report.reload()} />
{:else if !result}
	<Loading />
{:else if result.total === 0}
	<div class="card">
		<EmptyState message={filtered ? 'No images match these filters.' : 'No images yet.'} />
	</div>
{:else}
	<p class="small text-body-secondary mb-2">
		{formatCount(result.total)}
		{result.total === 1 ? 'image' : 'images'}
	</p>
	<div class="card">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col"><span class="visually-hidden">Preview</span></th>
						<th scope="col">Game</th>
						<th scope="col">System</th>
						<th scope="col">Type</th>
						<th scope="col">Resolution</th>
						<th scope="col">Size</th>
					</tr>
				</thead>
				<tbody>
					{#each result.items as item (item.imageId)}
						<tr>
							<td>
								<img
									src="/api/games/{item.gameId}/images/{item.imageId}"
									alt=""
									loading="lazy"
									style="max-width: 4rem; max-height: 4rem; object-fit: contain"
								/>
							</td>
							<td>
								<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query -->
								<a href="{resolve('/games/[id]', { id: item.gameId })}?tab=images"
									>{item.displayTitle}</a
								>
							</td>
							<td><SystemBadge system={item.system} name={item.systemName} /></td>
							<td>{imageTypeLabels[item.type]}</td>
							<td class="text-nowrap">{item.width} × {item.height}</td>
							<td><FileSize bytes={item.sizeBytes} /></td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
	<Pagination
		page={view.page}
		pages={pageCount(result.total, pageSize)}
		onpage={(p) => go(imageResolutionSearch({ ...view, page: p }))}
		label="Image pages"
	/>
{/if}
