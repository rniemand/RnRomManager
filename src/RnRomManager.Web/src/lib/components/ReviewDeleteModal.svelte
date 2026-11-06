<script lang="ts">
	import { api } from '../api/client';
	import type { ReviewDetail } from '../api/types';
	import { formatDateTime, formatKey, formatValue } from '../format';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import ErrorAlert from './ErrorAlert.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import Loading from './Loading.svelte';
	import SystemBadge from './SystemBadge.svelte';

	interface Props {
		open: boolean;
		/** The review item to delete; loaded each time the modal opens. */
		id: number | null;
		/** After a successful delete (the modal has closed). */
		ondeleted: (item: ReviewDetail) => void;
	}

	let { open = $bindable(), id, ondeleted }: Props = $props();

	let item = $state<ReviewDetail | null>(null);
	let error = $state<unknown>(null);
	let busy = $state(false);

	async function load(reviewId: number): Promise<void> {
		item = null;
		error = null;
		try {
			item = await api.get<ReviewDetail>(`/api/review/${reviewId}`);
		} catch (e) {
			error = e;
		}
	}

	$effect(() => {
		if (open && id !== null) void load(id);
	});

	async function confirm(): Promise<void> {
		if (!item) return;
		busy = true;
		try {
			await api.delete(`/api/review/${item.id}`);
			open = false;
			ondeleted(item);
		} catch (e) {
			if (!toasts.jobRunning(e)) error = e;
		} finally {
			busy = false;
		}
	}

	const hashes = $derived(
		item
			? [
					['CRC32', item.crc32],
					['MD5', item.md5],
					['SHA1', item.sha1]
				]
			: []
	);
</script>

<ConfirmModal
	bind:open
	title="Delete file"
	confirmLabel="Delete file"
	confirmClass="btn-danger"
	size="lg"
	busy={busy || !item}
	onconfirm={confirm}
>
	{#if error}
		<ErrorAlert {error} onretry={id !== null ? () => load(id) : undefined} />
	{:else if !item}
		<Loading />
	{:else}
		<p>This permanently deletes the file from the ingest folder. It can't be undone.</p>
		{#if item.archivePath}
			<div class="alert alert-warning d-flex gap-2" role="alert">
				<Icon name="warning" />
				<span
					>This deletes the whole archive <code>{item.archivePath}</code>, including every other
					file in it.</span
				>
			</div>
		{/if}
		<dl class="row small mb-0">
			<dt class="col-sm-3">File name</dt>
			<dd class="col-sm-9 text-break">{item.originalFileName}</dd>
			<dt class="col-sm-3">System</dt>
			<dd class="col-sm-9"><SystemBadge system={item.system} /></dd>
			<dt class="col-sm-3">Reason</dt>
			<dd class="col-sm-9">{item.reason}</dd>
			<dt class="col-sm-3">Size</dt>
			<dd class="col-sm-9"><FileSize bytes={item.sizeBytes} /></dd>
			{#each hashes as [label, value] (label)}
				<dt class="col-sm-3">{label}</dt>
				<dd class="col-sm-9 font-monospace text-break">{value}</dd>
			{/each}
			<dt class="col-sm-3">Source path</dt>
			<dd class="col-sm-9 text-break">{item.sourcePath}</dd>
			{#if item.archivePath}
				<dt class="col-sm-3">Archive</dt>
				<dd class="col-sm-9 text-break">{item.archivePath}</dd>
				<dt class="col-sm-3">Archive entry</dt>
				<dd class="col-sm-9 text-break">{item.archiveEntry}</dd>
			{/if}
			{#if item.header}
				{#each Object.entries(item.header) as [key, value] (key)}
					<dt class="col-sm-3">{formatKey(key)}</dt>
					<dd class="col-sm-9">{formatValue(value)}</dd>
				{/each}
			{/if}
			<dt class="col-sm-3">Added</dt>
			<dd class="col-sm-9">{formatDateTime(item.createdAt)}</dd>
		</dl>
	{/if}
</ConfirmModal>
