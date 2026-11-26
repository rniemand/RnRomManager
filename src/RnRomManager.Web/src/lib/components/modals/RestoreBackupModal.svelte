<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { formatDateTime } from '#lib/format.js';
	import type { BackupFile } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		/** B26: the backup being restored. */
		backup: BackupFile | null;
		busy?: boolean;
		/** The restore's failure, shown under the list. */
		error?: { title: string; detail?: string } | null;
		onconfirm: () => void;
	}

	let { open = $bindable(), backup, busy = false, error = null, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title="Restore backup"
	confirmLabel="Restore"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	{#if backup}
		<p>
			Restore <strong class="font-monospace">{backup.fileName}</strong> from {formatDateTime(
				backup.createdAt
			)}?
		</p>
		<ul>
			<li>Every table in the database is replaced by the backup's.</li>
			<li>A safety backup of the current database is taken first.</li>
			<li>Everyone, including you, is signed out.</li>
			<li>Run Integrity → Run quick afterwards: the library ZIPs may be newer than the backup.</li>
		</ul>
		{#if error}
			<div class="alert alert-danger mb-0" role="alert">
				<strong>{error.title}</strong>{#if error.detail}<div>
						{error.detail}
					</div>{/if}
			</div>
		{/if}
	{/if}
</BaseModal>
