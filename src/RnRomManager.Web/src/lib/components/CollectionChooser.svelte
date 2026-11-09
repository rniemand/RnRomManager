<script lang="ts">
	import type { Snippet } from 'svelte';
	import { onDestroy } from 'svelte';
	import { resolve } from '$app/paths';
	import type { CollectionListItem, Page } from '../api/types';
	import { collectionsApi } from '../collections';
	import ErrorAlert from './ErrorAlert.svelte';
	import Loading from './Loading.svelte';

	/**
	 * B25/B26: the caller's collections for a picker — a server-side name filter, the first 200 by name, a hint when
	 * there are more, and a link to the Collections page to make one. Each row is the caller's `item` snippet.
	 */
	interface Props {
		/** Loads while true (the modal is open). */
		active: boolean;
		item: Snippet<[CollectionListItem]>;
	}

	let { active, item }: Props = $props();

	const limit = 200;
	let filter = $state('');
	let q = $state('');
	let result = $state<Page<CollectionListItem> | null>(null);
	let error = $state<unknown>(null);
	let reload = $state(0);
	let timer: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(timer));
	const uid = Math.random().toString(36).slice(2);

	function oninput(): void {
		clearTimeout(timer);
		timer = setTimeout(() => (q = filter.trim()), 300);
	}

	$effect(() => {
		if (!active) {
			filter = q = '';
			return;
		}
		const params = { q, pageSize: limit };
		void reload;
		let stale = false;
		error = null;
		result = null;
		collectionsApi.list(params).then(
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

<label class="visually-hidden" for="collection-chooser-{uid}">Filter collections</label>
<input
	id="collection-chooser-{uid}"
	type="search"
	class="form-control mb-2"
	placeholder="Filter by name"
	maxlength="255"
	bind:value={filter}
	{oninput}
/>
{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if !result}
	<Loading label="Loading collections…" />
{:else if result.items.length === 0}
	<p class="text-body-secondary">
		{q ? `No collections match “${q}”.` : 'You have no collections yet.'}
	</p>
{:else}
	<div class="list-group chooser-list">
		{#each result.items as collection (collection.id)}
			{@render item(collection)}
		{/each}
	</div>
	{#if result.total > limit}
		<p class="form-text mb-0">Showing the first {limit} — filter to find more.</p>
	{/if}
{/if}
<p class="mt-2 mb-0 small">
	<a href={resolve('/collections')}>Create or manage collections</a>
</p>

<style>
	.chooser-list {
		max-height: 50vh;
		overflow-y: auto;
	}
</style>
