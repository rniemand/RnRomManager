<script lang="ts">
	import type { DatSearchResult, ReviewDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import SearchInput from '#lib/components/forms/SearchInput.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';

	interface Props {
		item: ReviewDetail;
		/** G8: a titledb (Switch) item; a pick is as verified as an automatic match. */
		titleDb: boolean;
		/** B8: the "update" checkbox's state. */
		asUpdate: boolean;
		disabled: boolean;
		/** B11/B12: the next queue item for Use and Next. */
		next: number | 'loading' | 'none' | 'failed';
		/** Bound: the DAT search box. */
		searchText: string;
		searchResults: DatSearchResult[] | null;
		searching: boolean;
		onupdatechange: (checked: boolean) => void;
		onuse: (datGameName: string, andNext?: boolean) => unknown;
		onsearch: (event: SubmitEvent) => unknown;
	}

	let {
		item,
		titleDb,
		asUpdate,
		disabled,
		next,
		searchText = $bindable(),
		searchResults,
		searching,
		onupdatechange,
		onuse,
		onsearch
	}: Props = $props();

	const percent = (score: number) => `${Math.round(score * 100)}%`;
</script>

<p class="small text-body-secondary">
	{#if titleDb}
		Choose a titledb result to identify this release by title ID.
	{:else}
		Picking a DAT entry adds the file as an unverified release, because its hashes differ.
	{/if}
</p>
{#if titleDb}
	<div class="form-check mb-3">
		<Checkbox
			id="as-update"
			checked={asUpdate}
			onchange={(e) => onupdatechange(e.currentTarget.checked)}
		/>
		<label class="form-check-label" for="as-update">This file is an update</label>
	</div>
{/if}
{#if item.suggestions?.length}
	<DataTable items={item.suggestions} key={(suggestion) => suggestion.romName} class="mb-3">
		{#snippet head()}
			<tr class="small text-uppercase text-body-secondary">
				<th scope="col">DAT entry</th>
				<th scope="col" class="text-end">Score</th>
				<th scope="col" class="text-end"><span class="visually-hidden">Actions</span></th>
			</tr>
		{/snippet}
		{#snippet row(suggestion)}
			<tr>
				<td class="text-break">{suggestion.romName}</td>
				<td class="text-end text-nowrap">{percent(suggestion.score)}</td>
				<td class="text-end text-nowrap">
					<Button
						variant="primary"
						size="sm"
						{disabled}
						onclick={() => onuse(suggestion.datGameName)}>Use</Button
					>
					<Button
						variant="primary"
						outline
						size="sm"
						class="ms-1"
						disabled={disabled || typeof next !== 'number'}
						title={next === 'none' ? 'No more items in the review queue.' : undefined}
						onclick={() => onuse(suggestion.datGameName, true)}>Use and Next</Button
					>
				</td>
			</tr>
		{/snippet}
	</DataTable>
{:else}
	<p class="text-body-secondary">No close DAT matches.</p>
{/if}

<form class="row g-2 align-items-end" role="search" onsubmit={onsearch}>
	<div class="col">
		<label class="form-label" for="dat-search">Search the {item.system} DAT</label>
		<SearchInput id="dat-search" bind:value={searchText} />
	</div>
	<div class="col-auto">
		<Button variant="secondary" outline type="submit" disabled={searching}>Search</Button>
	</div>
</form>
{#if searchResults}
	{#if searchResults.length}
		<ul class="list-group mt-2">
			{#each searchResults as result (result.romName)}
				<li class="list-group-item d-flex align-items-center gap-2">
					<span class="me-auto text-break">{result.romName}</span>
					{#if !(titleDb && result.sizeBytes === 0)}
						<!-- G8: titledb results carry no size (T3). -->
						<span class="small text-body-secondary text-nowrap"
							><FileSize bytes={result.sizeBytes} /></span
						>
					{/if}
					<Button variant="primary" size="sm" {disabled} onclick={() => onuse(result.gameName)}
						>Use</Button
					>
				</li>
			{/each}
		</ul>
	{:else}
		<p class="text-body-secondary mt-2">No DAT entries match.</p>
	{/if}
{/if}
