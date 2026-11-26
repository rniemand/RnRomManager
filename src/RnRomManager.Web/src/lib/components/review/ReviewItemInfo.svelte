<script lang="ts">
	import { resolve } from '$app/paths';
	import type { ReviewDetail } from '#lib/types/index.js';
	import CopyButton from '#lib/components/forms/CopyButton.svelte';
	import FileSize from '#lib/components/common/FileSize.svelte';
	import { formatDateTime, formatKey, formatValue } from '#lib/format.js';

	interface Props {
		item: ReviewDetail;
	}

	let { item }: Props = $props();
</script>

<div class="card mb-4">
	<div class="card-header"><h2 class="h6 mb-0">File</h2></div>
	<div class="card-body">
		<dl class="row small mb-0">
			<dt class="col-4">Size</dt>
			<dd class="col-8"><FileSize bytes={item.sizeBytes} /></dd>
			{#each [['CRC32', item.crc32], ['MD5', item.md5], ['SHA1', item.sha1]] as [label, value] (label)}
				<dt class="col-4">{label}</dt>
				<dd class="col-8 font-monospace text-break">
					{value}
					<CopyButton {value} {label} />
				</dd>
			{/each}
			<dt class="col-4">Source</dt>
			<dd class="col-8 text-break">
				{item.archiveEntry ? `${item.archivePath} › ${item.archiveEntry}` : item.sourcePath}
			</dd>
			<dt class="col-4">Added</dt>
			<dd class="col-8">{formatDateTime(item.createdAt)}</dd>
		</dl>
	</div>
</div>
{#if item.setMembers}
	<div class="card mb-4">
		<div class="card-header"><h2 class="h6 mb-0">Set</h2></div>
		<div class="card-body">
			<p class="small text-body-secondary">
				Resolving this item adds all {item.setMembers.length} files as one release.
			</p>
			<ul class="list-unstyled small mb-0">
				{#each item.setMembers as member (member.reviewId)}
					<li class="d-flex gap-2">
						{#if member.reviewId === item.id}
							<strong class="me-auto text-break">{member.fileName}</strong>
						{:else}
							<a
								class="me-auto text-break"
								href={resolve('/review/[id]', { id: String(member.reviewId) })}>{member.fileName}</a
							>
						{/if}
						<span class="text-body-secondary text-nowrap"
							><FileSize bytes={member.sizeBytes} /></span
						>
					</li>
				{/each}
			</ul>
		</div>
	</div>
{/if}
<div class="card mb-4">
	<div class="card-header"><h2 class="h6 mb-0">Header</h2></div>
	{#if item.header}
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr class="small text-uppercase text-body-secondary">
						<th scope="col">Field</th>
						<th scope="col">Value</th>
					</tr>
				</thead>
				<tbody>
					{#each Object.entries(item.header) as [key, value] (key)}
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
							<td>{formatValue(value, { counts: true })}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{:else}
		<div class="card-body">
			<p class="text-body-secondary mb-0">The header couldn't be read.</p>
		</div>
	{/if}
</div>
