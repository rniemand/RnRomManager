<script lang="ts" generics="K">
	import Button from '#lib/components/forms/Button.svelte';
	import Icon from '#lib/icons/Icon.svelte';
	import type { SortDir } from '#lib/enums/index.js';

	interface Props {
		/** One `<th>` each; a `sort: null` column is plain text. */
		columns: readonly { label: string; sort: K | null; class?: string }[];
		/** The active column's key. */
		sort: K | null;
		dir: SortDir;
		/** A sortable column was clicked; the site decides the new direction. */
		onsort: (key: K) => void;
	}

	let { columns, sort, dir, onsort }: Props = $props();
</script>

<!-- Only the header cells: the site keeps its <tr> and any extra <th>. -->
{#each columns as column (column.label)}
	{@const key = column.sort}
	<th
		scope="col"
		class={column.class}
		aria-sort={key !== null && key === sort
			? dir === 'asc'
				? 'ascending'
				: 'descending'
			: undefined}
	>
		{#if key !== null}
			<Button
				variant="link"
				class="p-0 text-reset text-decoration-none fw-semibold"
				onclick={() => onsort(key)}
				>{column.label}
				{#if key === sort}<Icon name={dir === 'asc' ? 'sort-asc' : 'sort-desc'} />{/if}</Button
			>
		{:else}
			{column.label}
		{/if}
	</th>
{/each}
