<script lang="ts">
	import { pageSizes } from '../pageSize';

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
			<button
				type="button"
				class="btn btn-sm btn-outline-secondary"
				disabled={page <= 1}
				onclick={() => onpage(page - 1)}>Previous</button
			>
			<span class="small">Page {page} of {pages}</span>
			<button
				type="button"
				class="btn btn-sm btn-outline-secondary"
				disabled={page >= pages}
				onclick={() => onpage(page + 1)}>Next</button
			>
		{/if}
		{#if onpagesize}
			<div class="d-flex align-items-center gap-2">
				<label class="small text-body-secondary text-nowrap" for={id}>Rows per page</label>
				<select
					{id}
					class="form-select form-select-sm w-auto"
					value={pageSize}
					onchange={(e) => onpagesize(Number(e.currentTarget.value))}
				>
					{#each pageSizes as size (size)}
						<option value={size}>{size}</option>
					{/each}
				</select>
			</div>
		{/if}
	</nav>
{/if}
