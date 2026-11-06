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
	title={item ? `Delete File: ${item.originalFileName}` : 'Delete File'}
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
		<table class="table table-sm table-striped table-hover small mb-0">
			<caption class="visually-hidden">Details of {item.originalFileName}</caption>
			<tbody>
				<tr
					><th scope="row" class="w-25">File name</th><td class="text-break"
						>{item.originalFileName}</td
					></tr
				>
				<tr><th scope="row">System</th><td><SystemBadge system={item.system} /></td></tr>
				<tr><th scope="row">Reason</th><td>{item.reason}</td></tr>
				<tr><th scope="row">Size</th><td><FileSize bytes={item.sizeBytes} /></td></tr>
				{#each hashes as [label, value] (label)}
					<tr><th scope="row">{label}</th><td class="font-monospace text-break">{value}</td></tr>
				{/each}
				<tr><th scope="row">Source path</th><td class="text-break">{item.sourcePath}</td></tr>
				{#if item.archivePath}
					<tr><th scope="row">Archive</th><td class="text-break">{item.archivePath}</td></tr>
					<tr><th scope="row">Archive entry</th><td class="text-break">{item.archiveEntry}</td></tr>
				{/if}
				{#if item.header}
					{#each Object.entries(item.header) as [key, value] (key)}
						<tr><th scope="row">{formatKey(key)}</th><td>{formatValue(value)}</td></tr>
					{/each}
				{/if}
				<tr><th scope="row">Added</th><td>{formatDateTime(item.createdAt)}</td></tr>
			</tbody>
		</table>
	{/if}
</ConfirmModal>
