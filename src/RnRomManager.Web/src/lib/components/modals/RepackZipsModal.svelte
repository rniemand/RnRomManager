<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import RepackWarning from './RepackWarning.svelte';
	import type { RomStorageRow } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		row: RomStorageRow | null;
		onconfirm: () => void;
	}

	let { open = $bindable(), row, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title={row ? `Repack ${row.displayName} ZIPs` : ''}
	confirmLabel="Repack"
	confirmClass="btn-warning"
	{onconfirm}
>
	{#if row}
		<RepackWarning name={row.displayName} />
	{/if}
</BaseModal>
