<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { formatCount } from '#lib/format.js';
	import { modalListSize } from '#lib/ingestDelete.js';
	import type { IngestDeleteEntry } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		/** The Deleted page's files to remove for good. */
		entries: IngestDeleteEntry[];
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), entries, busy = false, onconfirm }: Props = $props();

	const files = (n: number) => `${formatCount(n)} ${n === 1 ? 'file' : 'files'}`;
</script>

<BaseModal
	bind:open
	title="Permanently delete {files(entries.length)}?"
	confirmLabel="Delete"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	<p>This can't be undone.</p>
	<ul class="mb-0 text-break">
		{#each entries.slice(0, modalListSize) as entry (`${entry.system}/${entry.name}`)}
			<li><code>{entry.system}/{entry.name}</code></li>
		{/each}
		{#if entries.length > modalListSize}
			<li>and {entries.length - modalListSize} more</li>
		{/if}
	</ul>
</BaseModal>
