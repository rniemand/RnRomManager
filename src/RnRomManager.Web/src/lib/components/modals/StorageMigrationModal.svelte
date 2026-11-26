<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import type { RomStorageRow } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		/** The system being moved; its `embedRoms` is today's storage. */
		row: RomStorageRow | null;
		onconfirm: () => void;
	}

	let { open = $bindable(), row, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title={row ? `${row.embedRoms ? 'Store beside ZIP' : 'Store in ZIP'}: ${row.displayName}` : ''}
	confirmLabel="Start moving"
	confirmClass="btn-warning"
	{onconfirm}
>
	{#if row}
		<p>
			Every game of {row.displayName} is rewritten, one at a time. This takes a long time (hours for a
			large library) and is heavy on disk and CPU. Free space of about one game's ROMs plus its ZIP is
			needed.
		</p>
		<p>
			Until it ends, no other job (ingest, integrity, scheduled checks) can run and no game of any
			system can be edited. Games it doesn't reach (cancelled or failed) stay read-only until they
			are moved. You can cancel it from the Jobs page and resume it later; games already moved stay
			moved.
		</p>
		{#if row.embedRoms}
			<p class="mb-0">Each ROM becomes a separate file next to its ZIP.</p>
		{:else}
			<p class="mb-0">
				Saving a game state or battery save for this system then rewrites the whole ZIP, including
				its ROM (up to 512 MiB), so saves get slower.
			</p>
		{/if}
	{/if}
</BaseModal>
