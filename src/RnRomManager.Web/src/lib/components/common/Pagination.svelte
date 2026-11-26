<script lang="ts">
	import Select from '#lib/components/forms/Select.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import { pageSizes } from '#lib/pageSize.js';

	interface Props {
		page: number;
		pages: number;
		onpage: (page: number) => void;
		/** Current size; the selector shows only when `onpagesize` is given. */
		pageSize?: number;
		onpagesize?: (size: number) => void;
		label?: string;
	}

	let { page, pages, onpage, pageSize, onpagesize, label = 'Pages' }: Props = $props();

	const id = `page-size-${Math.random().toString(36).slice(2)}`;
</script>

{#if pages > 1 || onpagesize}
	<nav
		class="d-flex flex-wrap justify-content-center align-items-center gap-3 mt-3"
		aria-label={label}
	>
		{#if pages > 1}
			<Button
				variant="secondary"
				outline
				size="sm"
				disabled={page <= 1}
				onclick={() => onpage(page - 1)}>Previous</Button
			>
			<span class="small">Page {page} of {pages}</span>
			<Button
				variant="secondary"
				outline
				size="sm"
				disabled={page >= pages}
				onclick={() => onpage(page + 1)}>Next</Button
			>
		{/if}
		{#if onpagesize}
			<div class="d-flex align-items-center gap-2">
				<label class="small text-body-secondary text-nowrap" for={id}>Rows per page</label>
				<Select
					size="sm"
					class="w-auto"
					{id}
					value={pageSize}
					onchange={(e) => onpagesize(Number(e.currentTarget.value))}
					>{#each pageSizes as size (size)}
						<option value={size}>{size}</option>
					{/each}</Select
				>
			</div>
		{/if}
	</nav>
{/if}
