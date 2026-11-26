<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { modalListSize } from '#lib/ingestDelete.js';
	import type { ReviewListItem } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		/** The selected review items. */
		items: ReviewListItem[];
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), items, busy = false, onconfirm }: Props = $props();

	const plural = (n: number) => (n === 1 ? 'file' : 'files');
</script>

<BaseModal
	bind:open
	title="Delete {items.length} review {plural(items.length)}?"
	confirmLabel="Delete"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	<ul class="mb-2">
		{#each items.slice(0, modalListSize) as item (item.id)}
			<li class="text-break">{item.fileName}</li>
		{/each}
	</ul>
	{#if items.length > modalListSize}
		<p class="mb-2">… and {items.length - modalListSize} more</p>
	{/if}
	{#if items.some((i) => i.isArchive)}
		<p class="mb-0 text-danger">
			Archive entries delete their whole archive, including its other pending items.
		</p>
	{/if}
</BaseModal>
