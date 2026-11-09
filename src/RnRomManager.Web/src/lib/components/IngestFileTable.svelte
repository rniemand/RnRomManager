<script lang="ts">
	import type { SvelteSet } from 'svelte/reactivity';
	import { formatDateTime } from '../format';
	import { fileKey } from '../ingestDelete';
	import { pageCount } from '../pageSize';
	import { tooltip } from '../tooltip';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import Pagination from './Pagination.svelte';

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
	}

	let {
		system,
		files,
		selected,
		label = `${system} files`,
		showReason = false,
		disabled,
		ondelete
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

<div class="card">
	<div class="table-responsive">
		<table class="table table-sm table-striped table-hover align-middle mb-0">
			<thead class="table-dark">
				<tr class="small text-uppercase text-body-secondary">
					<th scope="col">
						<input
							class="form-check-input"
							type="checkbox"
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
			</thead>
			<tbody>
				{#each files.slice((current - 1) * pageSize, current * pageSize) as file (file.name)}
					<tr>
						<td>
							<input
								class="form-check-input"
								type="checkbox"
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
						<td class="text-end">
							<button
								type="button"
								class="btn btn-sm btn-outline-danger"
								aria-label="Delete {file.name}"
								use:tooltip={'Delete (Shift+click to skip confirmation)'}
								{disabled}
								onclick={(e) => ondelete(e, file.name)}
							>
								<Icon name="delete" />
							</button>
						</td>
					</tr>
				{/each}
			</tbody>
		</table>
	</div>
</div>
<Pagination
	page={current}
	pages={pageCount(files.length, pageSize)}
	onpage={(p) => (requested = p)}
	label="Pages of {label}"
/>
