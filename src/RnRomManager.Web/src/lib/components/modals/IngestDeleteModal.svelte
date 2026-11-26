<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { deleteTitle, modalListSize } from '#lib/ingestDelete.js';
	import type { IngestDeleteEntry } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		/** The ingest folder's files to delete. */
		entries: IngestDeleteEntry[];
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), entries, busy = false, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title={deleteTitle(entries)}
	confirmLabel="Delete"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	<p>
		{entries.length === 1 ? 'This file is' : 'These files are'} permanently deleted from the ingest folder.
		This can't be undone.
		{#if entries.some((e) => e.folder === 'pending')}
			Their pending Review items are deleted too.
		{/if}
	</p>
	<ul class="mb-0 text-break">
		{#each entries.slice(0, modalListSize) as entry (`${entry.folder}/${entry.system}/${entry.name}`)}
			<li><code>{entry.system}/{entry.name}</code></li>
		{/each}
		{#if entries.length > modalListSize}
			<li>and {entries.length - modalListSize} more</li>
		{/if}
	</ul>
</BaseModal>
