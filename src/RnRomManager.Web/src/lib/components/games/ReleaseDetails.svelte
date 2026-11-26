<script lang="ts">
	import type { RomDetail } from '#lib/types/index.js';
	import CopyButton from '#lib/components/forms/CopyButton.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import { formatDateTime } from '#lib/format.js';

	interface Props {
		rom: RomDetail;
	}

	let { rom }: Props = $props();

	const list = (items: string[]) => (items.length ? items.join(', ') : '—');

	/** R2: how a release was identified. */
	const identifiedBy = {
		Dat: 'DAT',
		Manual: 'Manual',
		TitleDb: 'Title DB',
		Serial: 'Game ID'
	} as const;
</script>

<!-- B8: a key/value table (T1–T4), one pair per row, each row only when it has a value. Values
     keep wrapping (G2: never truncated), an exception to the T-rule's text-truncate. -->
<div class="card">
	<div class="table-responsive">
		<table class="table table-sm table-striped table-hover mb-0">
			<tbody>
				{#if rom.originalFileName !== rom.fileName}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Original file name</th>
						<td class="text-break">
							<span class="text-break">{rom.originalFileName}</span>
							<CopyButton value={rom.originalFileName} label="original file name" />
						</td>
					</tr>
				{/if}
				<tr>
					<th scope="row" class="fw-normal text-body-secondary">Size</th>
					<td class="text-break"><FileSize bytes={rom.totalSizeBytes} /></td>
				</tr>
				{#each [['SHA1', rom.sha1], ['CRC32', rom.crc32], ['MD5', rom.md5]] as [name, hash] (name)}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">{name}</th>
						<td class="text-break">
							<span class="font-monospace text-break">{hash}</span>
							<CopyButton value={hash} label={name} />
						</td>
					</tr>
				{/each}
				{#if rom.revision}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Revision</th>
						<td class="text-break">{rom.revision}</td>
					</tr>
				{/if}
				{#if rom.languages.length}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Languages</th>
						<td class="text-break">{list(rom.languages)}</td>
					</tr>
				{/if}
				{#if rom.flags.length}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">Flags</th>
						<td class="text-break">{list(rom.flags)}</td>
					</tr>
				{/if}
				<tr>
					<th scope="row" class="fw-normal text-body-secondary">Identified by</th>
					<td class="text-break">{identifiedBy[rom.identificationMethod]}</td>
				</tr>
				{#if rom.datName}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">DAT</th>
						<td class="text-break">
							{[rom.datName, rom.datVersion].filter(Boolean).join(' ')}
						</td>
					</tr>
				{/if}
				{#if rom.datGameName}
					<tr>
						<th scope="row" class="fw-normal text-body-secondary">DAT entry</th>
						<td class="text-break">{rom.datGameName}</td>
					</tr>
				{/if}
				<tr>
					<th scope="row" class="fw-normal text-body-secondary">Date added</th>
					<td class="text-break">{formatDateTime(rom.dateAdded)}</td>
				</tr>
			</tbody>
		</table>
	</div>
</div>
