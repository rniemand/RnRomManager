<script lang="ts" generics="T">
	import type { Snippet } from 'svelte';
	import type { HTMLTableAttributes } from 'svelte/elements';
	import EmptyState from './EmptyState.svelte';

	interface Props extends Omit<HTMLTableAttributes, 'class'> {
		items: readonly T[];
		/** The header row(s), inside `<thead class="table-dark">`. */
		head: Snippet;
		/** One body row per item. */
		row: Snippet<[T, number]>;
		/** Each row's key (default: its index). */
		key?: (item: T) => unknown;
		/** Shown through `EmptyState` in place of the card when there are no items. */
		empty?: string;
		/** Shows `empty` inside the card (in place of the table) instead of in place of the card. */
		emptyInCard?: boolean;
		/** Appended to the card's `card`. */
		class?: string;
		/** Appended to the table's classes. */
		tableClass?: string;
	}

	let {
		items,
		head,
		row,
		key,
		empty,
		emptyInCard = false,
		class: className,
		tableClass,
		...rest
	}: Props = $props();

	const isEmpty = $derived(empty !== undefined && items.length === 0);
</script>

{#if isEmpty && !emptyInCard}
	<EmptyState message={empty!} />
{:else}
	<div class={['card', className]}>
		<div class="table-responsive">
			{#if isEmpty}
				<EmptyState message={empty!} />
			{:else}
				<!-- A quoted class, so table-style.test.ts can read it. -->
				<table
					class="table table-sm table-striped table-hover align-middle mb-0{tableClass
						? ` ${tableClass}`
						: ''}"
					{...rest}
				>
					<thead class="table-dark">{@render head()}</thead>
					<tbody>
						{#each items as item, i (key ? key(item) : i)}
							{@render row(item, i)}
						{/each}
					</tbody>
				</table>
			{/if}
		</div>
	</div>
{/if}
