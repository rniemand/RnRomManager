<script lang="ts">
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import { fromAction } from 'svelte/attachments';
	import Button from '#lib/components/forms/Button.svelte';
	import DataTable from '#lib/components/common/DataTable.svelte';
	import type { SvelteSet } from 'svelte/reactivity';
	import { formatDateTime } from '#lib/format.js';
	import { fileKey } from '#lib/ingestDelete.js';
	import { pageCount } from '#lib/pageSize.js';
	import { tooltip } from '#lib/tooltip.js';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import Pagination from '#lib/components/common/Pagination.svelte';

	interface IngestFileRow {
		name: string;
		sizeBytes: number;
		modifiedAt: string;
		reason?: string | null;
	}

	interface Props {
		system: string;
		files: IngestFileRow[];
		/** Keys from {@link fileKey}; shared with the section's toolbar. */
		selected: SvelteSet<string>;
		/** Names the table in its labels; defaults to `<system> files`. */
		label?: string;
		/** D4: the Failed section shows each file's reason. */
		showReason?: boolean;
		/** Disables the delete buttons (a job runs or a delete is in flight). */
		disabled: boolean;
		ondelete: (event: MouseEvent, name: string) => void;
		/** Deleted ROMs (D3): a Restore button before Delete on each row; the Ingestion page leaves it out. */
		onrestore?: (event: MouseEvent, name: string) => void;
	}

	let {
		system,
		files,
		selected,
		label = `${system} files`,
		showReason = false,
		disabled,
		ondelete,
		onrestore
	}: Props = $props();

	/** G1/G2: fixed 20 rows per page, no selector. */
	const pageSize = 20;
	let requested = $state(1);
	const current = $derived(Math.min(requested, pageCount(files.length, pageSize)));
	const count = $derived(files.filter((f) => selected.has(fileKey(system, f.name))).length);
	const checkState = $derived(count === 0 ? 'none' : count === files.length ? 'all' : 'some');

	function toggleAll(): void {
		const next = checkState !== 'all';
		for (const f of files) {
			if (next) selected.add(fileKey(system, f.name));
			else selected.delete(fileKey(system, f.name));
		}
	}

	function toggle(k: string): void {
		if (!selected.delete(k)) selected.add(k);
	}
</script>

<DataTable
	items={files.slice((current - 1) * pageSize, current * pageSize)}
	key={(file) => file.name}
>
	{#snippet head()}
		<tr class="small text-uppercase text-body-secondary">
			<th scope="col">
				<Checkbox
					aria-label="Select all {label}"
					checked={checkState === 'all'}
					indeterminate={checkState === 'some'}
					onchange={toggleAll}
				/>
			</th>
			<th scope="col">Name</th>
			{#if showReason}<th scope="col">Reason</th>{/if}
			<th scope="col" class="text-end">Size</th>
			<th scope="col">Modified</th>
			<th scope="col"><span class="visually-hidden">Actions</span></th>
		</tr>
	{/snippet}
	{#snippet row(file)}
		<tr>
			<td>
				<Checkbox
					aria-label="Select {file.name}"
					checked={selected.has(fileKey(system, file.name))}
					onchange={() => toggle(fileKey(system, file.name))}
				/>
			</td>
			<td class="text-truncate" style="max-width: 32rem" title={file.name}>{file.name}</td>
			{#if showReason}
				<td
					>{#if file.reason}<code>{file.reason}</code>{:else}—{/if}</td
				>
			{/if}
			<td class="text-end"><FileSize bytes={file.sizeBytes} /></td>
			<td class="text-nowrap">{formatDateTime(file.modifiedAt)}</td>
			<td class="text-end text-nowrap">
				{#if onrestore}
					<Button
						variant="primary"
						outline
						size="sm"
						icon="restore"
						aria-label="Restore {file.name}"
						{disabled}
						onclick={(e) => onrestore(e, file.name)}
					/>
				{/if}
				<Button
					variant="danger"
					outline
					size="sm"
					icon="delete"
					aria-label="Delete {file.name}"
					{@attach fromAction(tooltip, () => 'Delete (Shift+click to skip confirmation)')}
					{disabled}
					onclick={(e) => ondelete(e, file.name)}
				/>
			</td>
		</tr>
	{/snippet}
</DataTable>
<Pagination
	page={current}
	pages={pageCount(files.length, pageSize)}
	onpage={(p) => (requested = p)}
	label="Pages of {label}"
/>
