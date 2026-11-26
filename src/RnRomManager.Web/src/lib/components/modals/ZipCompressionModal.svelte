<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import RepackWarning from './RepackWarning.svelte';
	import FormField from '#lib/components/forms/FormField.svelte';
	import Select from '#lib/components/forms/Select.svelte';
	import type { RomStorageRow } from '#lib/types/index.js';

	type Level = RomStorageRow['zipCompression'];

	interface Props {
		open: boolean;
		row: RomStorageRow | null;
		/** The chosen level; confirming is disabled while it equals the row's. */
		level: Level;
		levels: { value: Level; label: string }[];
		onconfirm: () => void;
	}

	let { open = $bindable(), row, level = $bindable(), levels, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title={row ? `ZIP compression: ${row.displayName}` : ''}
	confirmLabel="Change and repack"
	confirmClass="btn-warning"
	confirmDisabled={level === row?.zipCompression}
	{onconfirm}
>
	{#if row}
		<div class="mb-3">
			<FormField label="Compression level" for="zip-compression-level">
				<Select
					id="zip-compression-level"
					bind:value={level}
					options={levels.map((l) => ({ value: l.value, label: l.label }))}
				/>
			</FormField>
		</div>
		<RepackWarning name={row.displayName} />
	{/if}
</BaseModal>
