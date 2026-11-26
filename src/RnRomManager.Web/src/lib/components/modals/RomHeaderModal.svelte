<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { formatKey, formatValue } from '#lib/format.js';
	import type { RomDetail } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		rom: RomDetail | null;
	}

	let { open = $bindable(), rom }: Props = $props();

	/** R3: the header's key/value pairs, two per table row. */
	function headerRows(header: Record<string, unknown>): [string, unknown][][] {
		const entries = Object.entries(header);
		const rows: [string, unknown][][] = [];
		for (let i = 0; i < entries.length; i += 2) rows.push(entries.slice(i, i + 2));
		return rows;
	}
</script>

<BaseModal bind:open title="Header — {rom?.fileName ?? ''}" size="lg">
	{#if rom?.header}
		<table class="table table-sm table-striped table-hover mb-0">
			<caption class="visually-hidden">Header of {rom.fileName}</caption>
			<tbody>
				<!-- R3: two pairs per row; an odd last pair leaves the second half empty. -->
				{#each headerRows(rom.header) as pairs (pairs[0][0])}
					<tr>
						{#each pairs as [key, value] (key)}
							<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
							<td>{formatValue(value)}</td>
						{/each}
						{#if pairs.length === 1}<td colspan="2"></td>{/if}
					</tr>
				{/each}
			</tbody>
		</table>
	{/if}
</BaseModal>
